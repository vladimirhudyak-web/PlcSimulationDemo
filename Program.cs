// ============================================================================
//  PlcSimulationDemo — симуляция схемы «Приложение → Сервер C# → PLC → железо»
//  .NET 8, один файл, без внешних библиотек.
//
//  Как запустить:   dotnet run
//
//  Что внутри:
//   * PlcSimulator  — «контроллер»: TCP-сервер с регистрами (как в Modbus)
//                     и циклом сканирования: входы → логика → выходы.
//   * ParkingServer — «сервер на C#»: очередь заказов, пишет команды
//                     в регистры PLC и опрашивает его состояние.
//   * Main          — «приложение / киоск»: клиенты заказывают машины.
// ============================================================================

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

Console.OutputEncoding = Encoding.UTF8;
const int PlcPort = 5020;

// 1. Запускаем «контроллер»
//1. Run(start) Controller
var plc = new PlcSimulator(PlcPort);
plc.Start();

// 2. Сервер подключается к контроллеру по TCP
//2 . Server connects to the controller via TCP
var server = new ParkingServer("127.0.0.1", PlcPort);
await server.ConnectAsync();
Task serverTask = server.RunAsync();

// 3. «Приложение / киоск»: клиенты просят выдать машины
//3. «Application / kiosk»: clients request cars
await server.RequestCarAsync(57);
await server.RequestCarAsync(12);
await server.RequestCarAsync(999);   // такой ячейки нет → PLC вернёт ошибку

server.CompleteRequests();           // больше заказов не будет
await serverTask;                    // ждём, пока всё обработается
plc.Stop();

//Logger.Log("APP", "Все заказы обработаны. Нажмите Enter для выхода.");
Logger.Log("APP", "ALL orders processed. Press Enter to exit.");
Console.ReadLine();


// ============================================================================
//  Карта регистров — общий «договор» между сервером и PLC
// ============================================================================
//  Регистры 0..255, тип ushort (16 бит). Сервер и PLC читают/пишут их по TCP.

static class Reg
{
    public const int TargetCell = 100;   // сервер пишет: номер ячейки
    public const int Command    = 101;   // сервер пишет 1 = «выдать машину»; PLC сбрасывает в 0, когда принял
    public const int Status     = 200;   // PLC пишет: текущее состояние (PlcStatus)
    public const int Position   = 201;   // PLC пишет: позиция робота
    public const int ErrorCode  = 202;   // PLC пишет: код ошибки (1 = нет такой ячейки)
}

enum PlcStatus : ushort
{
    Idle         = 0,   // ожидание
    MovingToCell = 1,   // робот едет к ячейке
    LiftingCar   = 2,   // забирает машину
    MovingToExit = 3,   // везёт машину к выезду
    Done         = 4,   // машина выдана
    Error        = 9    // ошибка
}


// ============================================================================
//  PLC-симулятор
// ============================================================================
sealed class PlcSimulator
{
    private const int MaxCell = 100;
    private const int ExitPosition = 0;
    private const int ScanCycleMs = 50;

    private readonly ushort[] _registers = new ushort[256];   // память контроллера
    private readonly object _lock = new();
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    // --- «Железо»: датчики (входы) и мотор (выход) ---
    private int  _robotPosition;        // датчик позиции робота (энкодер)
    private bool _carOnPlatform;        // датчик «машина на платформе»
    private bool _motorOn;              // выход: мотор включён
    private int  _motorDirection;       // +1 вперёд, -1 назад
    private int  _liftTimer;            // сколько циклов ещё поднимаем машину

    //public PlcSimulator(int port) => _listener = new TcpListener(IPAddress.Loopback, port);
    //Update for test
    public PlcSimulator(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
    }


    public void Start()
    {
        _listener.Start();
        _ = Task.Run(() => ScanCycleLoopAsync(_cts.Token));
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Logger.Log("PLC", $"Controller started , port {((IPEndPoint)_listener.LocalEndpoint).Port}");
    }

    public void Stop()
    {
        _cts.Cancel();
        _listener.Stop();
    }

    // ---------- Цикл сканирования: входы → логика → выходы ----------
    private async Task ScanCycleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            lock (_lock)
            {
                // 1. Читаем входы (датчики)
                //1. Read inputs (sensors) 
                int position   = _robotPosition;
                bool carPresent = _carOnPlatform;

                // 2. Выполняем программу
                //2. Execute the program
                RunLogic(position, carPresent);

                // 3. Пишем выходы: включённый мотор двигает робота
                //3. Write outputs: the running motor moves the robot
                if (_motorOn) _robotPosition += _motorDirection;
                _registers[Reg.Position] = (ushort)_robotPosition;
            }

            try { await Task.Delay(ScanCycleMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Логика контроллера — конечный автомат
    // The controller logic is a finite state machine
    private void RunLogic(int position, bool carPresent)
    {
        var status = (PlcStatus)_registers[Reg.Status];
        int target = _registers[Reg.TargetCell];

        switch (status)
        {
            case PlcStatus.Idle:
            case PlcStatus.Done:
            case PlcStatus.Error:
                if (_registers[Reg.Command] != 1) break;

                _registers[Reg.Command] = 0;                 // команда принята
                if (target < 1 || target > MaxCell)
                {
                    _registers[Reg.ErrorCode] = 1;           // нет такой ячейки
                    SetStatus(PlcStatus.Error, position);
                    break;
                }
                _registers[Reg.ErrorCode] = 0;
                StartMotor(+1);
                SetStatus(PlcStatus.MovingToCell, position);
                break;

            case PlcStatus.MovingToCell:
                if (position == target)
                {
                    StopMotor();
                    _liftTimer = 20;                          // 20 циклов ≈ 1 сек
                    SetStatus(PlcStatus.LiftingCar, position);
                }
                break;

            case PlcStatus.LiftingCar:
                if (--_liftTimer <= 0)
                {
                    _carOnPlatform = true;                    // датчик: машина на платформе
                    StartMotor(-1);
                    SetStatus(PlcStatus.MovingToExit, position);
                }
                break;

            case PlcStatus.MovingToExit:
                if (position == ExitPosition && carPresent)
                {
                    StopMotor();
                    _carOnPlatform = false;                   // машину забрал клиент
                    SetStatus(PlcStatus.Done, position);
                }
                break;
        }
    }

    private void StartMotor(int direction) { _motorOn = true; _motorDirection = direction; }
    private void StopMotor()               { _motorOn = false; _motorDirection = 0; }

    private void SetStatus(PlcStatus s, int position)
    {
        _registers[Reg.Status] = (ushort)s;
        Logger.Log("PLC", $"State → {s} (position work : {position})");
    }

    // ---------- Сеть ----------
    //-------Network----------
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                Logger.Log("PLC", "Server is connected");
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    // Упрощённый протокол «как Modbus»: одна строка = одна команда.
    //   R <адрес>            → значение регистра
    //   W <адрес> <значение> → OK
    // Строки (\n) решают задачу framing: сообщение = до символа новой строки.
    //   
    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line is null) break;                  // соединение закрыто
                    await writer.WriteLineAsync(Execute(line));
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    private string Execute(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lock (_lock)
        {
            if (p.Length == 2 && p[0] == "R" &&
                int.TryParse(p[1], out int ra) && ra is >= 0 and < 256)
                return _registers[ra].ToString();

            if (p.Length == 3 && p[0] == "W" &&
                int.TryParse(p[1], out int wa) && wa is >= 0 and < 256 &&
                ushort.TryParse(p[2], out ushort value))
            {
                _registers[wa] = value;
                return "OK";
            }
        }
        return "ERR";
    }
}


// ============================================================================
//  Сервер на C#: очередь заказов + общение с PLC
// ============================================================================
//  Server in C#: order queue + communication with PLC

sealed class ParkingServer
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    // Очередь заказов: приложение кладёт, сервер обрабатывает по одному
    private readonly Channel<int> _requests = Channel.CreateUnbounded<int>();

    public ParkingServer(string host, int port) { _host = host; _port = port; }

    public async Task ConnectAsync()
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port);
        NetworkStream stream = _client.GetStream();
        _reader = new StreamReader(stream);
        _writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
        //Logger.Log("SERVER", $"Подключён к PLC {_host}:{_port}");
        Logger.Log("SERVER", $"Connected to PLC {_host}:{_port}");

    }

    public async Task RequestCarAsync(int cell)
    {
        //Logger.Log("APP", $"Клиент просит выдать машину из ячейки {cell}");
        Logger.Log("APP", $"Client -  get car from cell {cell}");
        await _requests.Writer.WriteAsync(cell);
    }

    public void CompleteRequests() => _requests.Writer.Complete();

    public async Task RunAsync()
    {
        await foreach (int cell in _requests.Reader.ReadAllAsync())
        {
            try
            {
                await RetrieveCarAsync(cell);
            }
            catch (Exception ex)
            {
                //Logger.Log("SERVER", $"Сбой при обработке ячейки {cell}: {ex.Message}");
                Logger.Log("SERVER", $"Error while processing cell {cell}: {ex.Message}");

            }
        }
        _client?.Close();
    }

    private async Task RetrieveCarAsync(int cell)
    {
        Logger.Log("SERVER", $"--- Order: cell {cell}. Send command in PLC ---");

        // 1. Пишем команду в регистры PLC
        // 1. Write command to PLC registers
        await WriteRegisterAsync(Reg.TargetCell, (ushort)cell);
        await WriteRegisterAsync(Reg.Command, 1);

        var deadline = DateTime.UtcNow.AddSeconds(30);   // таймаут на весь заказ

        // 2. Ждём, пока PLC примет команду (сбросит регистр Command в 0)
        // 2. Wait for PLC to accept the command (reset Command register to 0)
        while (await ReadRegisterAsync(Reg.Command) != 0)
        {
            //if (DateTime.UtcNow > deadline) { Logger.Log("SERVER", "Таймаут: PLC не принял команду"); return; }
            if (DateTime.UtcNow > deadline) { Logger.Log("SERVER", "Timeout: PLC did not accept command"); return; }
            await Task.Delay(50);
        }

        // 3. Опрашиваем состояние (polling), пока не Done или Error
        // 3. Polling the status until Done or Error
        PlcStatus? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var status = (PlcStatus)await ReadRegisterAsync(Reg.Status);
            if (status != last)
            {
                ushort pos = await ReadRegisterAsync(Reg.Position);
                //Logger.Log("SERVER", $"PLC сообщает: {status}, позиция {pos}");
                Logger.Log("SERVER", $"PLC messages: {status}, position {pos}");

                last = status;
            }

            if (status == PlcStatus.Done)
            {
                //Logger.Log("SERVER", $"✔ Машина из ячейки {cell} выдана. Обновляю БД и экран в лобби.");
                Logger.Log("SERVER", $"✔ Car from cell {cell} issued. Updating DB and lobby screen.");
                return;
            }
            if (status == PlcStatus.Error)
            {
                ushort code = await ReadRegisterAsync(Reg.ErrorCode);
                //Logger.Log("SERVER", $"✘ Ошибка PLC, код {code} (ячейка {cell}). Сообщаю оператору.");
                Logger.Log("SERVER", $"✘ Error PLC, код {code} (ячейка {cell}). Сообщаю оператору.");
                return;
            }
            await Task.Delay(200);
        }
        //Logger.Log("SERVER", $"✘ Таймаут: заказ по ячейке {cell} не завершён за 30 сек");
        Logger.Log("SERVER", $"✘ Timeout:  order for cell {cell} not completed within 30 seconds");

    }

    // ---------- Низкий уровень: команды протокола ----------
    // ---------- Low level: protocol commands ----------
    private async Task<string> SendAsync(string command)
    {
        await _writer!.WriteLineAsync(command);
        //return await _reader!.ReadLineAsync() ?? throw new IOException("PLC закрыл соединение");
        return await _reader!.ReadLineAsync() ?? throw new IOException("PLC closed the connection");
    }

    private async Task WriteRegisterAsync(int address, ushort value)
    {
        if (await SendAsync($"W {address} {value}") != "OK")
            //throw new InvalidOperationException($"Не удалось записать регистр {address}");
            throw new InvalidOperationException($"Not possible to write register {address}");
    }

    private async Task<ushort> ReadRegisterAsync(int address) =>
        ushort.Parse(await SendAsync($"R {address}"));
}


// ============================================================================
//  Цветной лог: видно, кто что делает
// ============================================================================
//  Color log: you can see who is doing what
static class Logger
{
    private static readonly object Sync = new();

    public static void Log(string source, string message)
    {
        lock (Sync)
        {
            Console.ForegroundColor = source switch
            {
                "APP"    => ConsoleColor.Yellow,
                "SERVER" => ConsoleColor.Cyan,
                "PLC"    => ConsoleColor.Green,
                _        => ConsoleColor.Gray
            };
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{source,-6}] {message}");
            Console.ResetColor();
        }
    }
}
