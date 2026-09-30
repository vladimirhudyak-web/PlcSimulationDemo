# PLC Simulation Demo (C# / .NET 8)

A small console application that simulates the architecture of an automated parking system:

```
App / Kiosk  →  C# Server  →  TCP/IP  →  PLC  →  Sensors & Actuators
```

## Components
- **PlcSimulator** — a simulated PLC: a TCP server with a register map (Modbus-like) and a scan cycle (inputs → logic → outputs). The control logic is a state machine: `Idle → MovingToCell → LiftingCar → MovingToExit → Done / Error`.
- **ParkingServer** — the C# server: a request queue (`System.Threading.Channels`), writes commands to PLC registers, waits for acknowledgment, polls status, handles timeouts and errors.
- **Main** — plays the role of the app/kiosk and places orders (including an invalid cell to demonstrate error handling).

## Protocol
A simplified, text-based, Modbus-like protocol (one line = one message):
- `R <address>` → returns the register value
- `W <address> <value>` → `OK`

| Register | Meaning |
|---|---|
| 100 | Target cell (written by server) |
| 101 | Command: 1 = retrieve car (PLC resets to 0 when accepted) |
| 200 | Status (written by PLC) |
| 201 | Robot position |
| 202 | Error code |

## Run
```
dotnet run
```

## Concepts demonstrated
TCP/IP client/server, message framing, register-based PLC communication, scan cycle, state machine, producer–consumer queue, thread safety (`lock`), async/await, timeouts and error handling.
