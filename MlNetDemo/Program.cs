using Microsoft.ML;
using Microsoft.ML.Data;

// Задача (регрессия): предсказать, сколько секунд робот-парковщик
// будет доставать машину, зная этаж ячейки и расстояние до неё.

var ml = new MLContext(seed: 42);

// 1. Данные для обучения (обычно грузятся из CSV/БД)
var rnd = new Random(1);
var samples = Enumerable.Range(0, 500).Select(_ =>
{
    float floor = rnd.Next(0, 10);        // этаж 0..9
    float distance = rnd.Next(5, 100);    // метры 5..99
    // "Истинная" зависимость + шум: 20 сек база, 8 сек на этаж, 0.5 сек на метр
    float seconds = 20 + 8 * floor + 0.5f * distance + (float)(rnd.NextDouble() * 6 - 3);
    return new ParkingRecord { Floor = floor, Distance = distance, Seconds = seconds };
}).ToList();

IDataView data = ml.Data.LoadFromEnumerable(samples);

// 2. Делим на train / test (80/20)
var split = ml.Data.TrainTestSplit(data, testFraction: 0.2);

// 3. Pipeline: признаки -> вектор "Features" -> нормализация -> алгоритм
var pipeline = ml.Transforms.Concatenate("Features", nameof(ParkingRecord.Floor), nameof(ParkingRecord.Distance))
    .Append(ml.Transforms.NormalizeMinMax("Features"))
    .Append(ml.Regression.Trainers.Sdca(labelColumnName: nameof(ParkingRecord.Seconds)));

// 4. Обучение
ITransformer model = pipeline.Fit(split.TrainSet);

// 5. Оценка качества на тестовых данных
var predictions = model.Transform(split.TestSet);
var metrics = ml.Regression.Evaluate(predictions, labelColumnName: nameof(ParkingRecord.Seconds));
Console.WriteLine($"R^2  = {metrics.RSquared:F3}   (1.0 = идеально)");
Console.WriteLine($"RMSE = {metrics.RootMeanSquaredError:F2} сек (средняя ошибка)");

// 6. Предсказание для новой машины
var engine = ml.Model.CreatePredictionEngine<ParkingRecord, ParkingPrediction>(model);
var request = new ParkingRecord { Floor = 5, Distance = 40 };
var result = engine.Predict(request);
Console.WriteLine($"\nЭтаж {request.Floor}, {request.Distance} м -> ~{result.Seconds:F1} сек");
Console.WriteLine($"(по формуле должно быть {20 + 8 * 5 + 0.5 * 40} сек)");

// 7. Сохранение модели в файл (потом можно загрузить через ml.Model.Load)
ml.Model.Save(model, data.Schema, "parking-model.zip");
Console.WriteLine("\nМодель сохранена в parking-model.zip");

// Входные данные: признаки (Features) + метка (Label)
public class ParkingRecord
{
    public float Floor { get; set; }
    public float Distance { get; set; }
    public float Seconds { get; set; }   // то, что учимся предсказывать
}

// Выход модели: регрессия кладёт результат в колонку "Score"
public class ParkingPrediction
{
    [ColumnName("Score")]
    public float Seconds { get; set; }
}
