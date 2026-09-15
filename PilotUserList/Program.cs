using Ascon.Pilot.Common;
using Ascon.Pilot.Common.DataProtection;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api;
using Ascon.Pilot.Server.Api.Contracts;
using Ascon.Pilot.Transport;
using System.Text;
using System.Text.Json;

namespace PilotConsole
{
    public class SearchCallback : IServerCallback
    {
        private readonly AutoResetEvent _waitHandle;
        public DSearchResult? Result { get; private set; }

        public SearchCallback(AutoResetEvent waitHandle)
        {
            _waitHandle = waitHandle;
        }

        public void NotifySearchResult(DSearchResult searchResult)
        {
            Result = searchResult;
            _waitHandle.Set();
        }

        // Остальные методы Notify... можно оставить пустыми, как у вас
        public void NotifyChangeset(DChangeset changeset) { }
        public void NotifyOrganisationUnitChangeset(OrganisationUnitChangeset changeset) { }
        public void NotifyPersonChangeset(PersonChangeset changeset) { }
        public void NotifyDMetadataChangeset(DMetadataChangeset changeset) { }
        public void NotifyGeometrySearchResult(DGeometrySearchResult searchResult) { }
        public void NotifyDNotificationChangeset(DNotificationChangeset changeset) { }
        public void NotifyCommandResult(Guid requestId, byte[] data, ServerCommandResult result) { }
        public void NotifyChangeAsyncCompleted(DChangeset changeset) { }
        public void NotifyChangeAsyncError(Guid identity, ProtoExceptionInfo exception) { }
        public void NotifyCustomNotification(string name, byte[] data) { }
    }

    internal class Program
    {
        private const int LoadBatchSize = 200;
        private const int ChangesetStep = 1000;

        private static async Task Main(string[] args)
        {
            string url = args.Length > 0 ? args[0] : "https://pilot-bim.rain.ru:2491";
            string databaseName = args.Length > 1 ? args[1] : "FAIRYTALE";
            string login = args.Length > 2 ? args[2] : "RAIN\\l.pupa";
            string password = args.Length > 3 ? args[3] : "nesn123";

            // ID целевой папки (если Guid.Empty — выгрузка всей базы)
            Guid folderId = new Guid("24e3fc9d-2ab2-47dc-a7ef-af13d65e5dde");


            // ID типов объектов для выгрузки (из вашего списка)
            var allowedTypeIds = new HashSet<int>
            {
                0, 1, 2, 9, 11, 13, 15, 17, 19, 28, 29, 30, 32, 34, 38, 41, 44, 49, 50, 51, 53, 54, 55, 56, 57, 58, 59,
                60, 61, 63, 66, 73, 74, 75, 76, 79, 80, 81, 82, 83, 84, 86, 88, 89, 90, 91, 97, 101, 104, 106, 110, 111,
                112, 115, 120, 127, 128, 130, 131, 132, 134, 136, 145, 146, 148, 150, 153, 154, 156, 157, 163, 164, 165, 171
            };


            try
            {
                // ---------- 1. Подключение и авторизация ----------
                var httpClient = new HttpPilotClient(url);
                httpClient.Connect(false);

                var waitHandle = new AutoResetEvent(false);
                var searchCallback = new SearchCallback(waitHandle);
                var serverApi = httpClient.GetServerApi(searchCallback);

                var auth = httpClient.GetAuthenticationApi();
                auth.Login(databaseName, login, password.EncryptAes(), false, 101);
                Console.WriteLine("Авторизация успешна.");

                var dbInfo = serverApi.OpenDatabase();
                Console.WriteLine($"База открыта. Версия метаданных: {dbInfo.MetadataVersion}");

                // ---------- 2. Пользователи и орг. единицы ----------
                var people = serverApi.LoadPeople();
                var orgUnits = serverApi.LoadOrganisationUnits();

                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                string userFilePath = Path.Combine(desktopPath, $"PilotUsers_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(userFilePath, JsonSerializer.Serialize(people, jsonOptions), Encoding.UTF8);
                Console.WriteLine($"Пользователи сохранены: {userFilePath}");

                string orgFilePath = Path.Combine(desktopPath, $"PilotOrgUnits_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(orgFilePath, JsonSerializer.Serialize(orgUnits, jsonOptions), Encoding.UTF8);
                Console.WriteLine($"Орг. единицы сохранены: {orgFilePath}");

                // ---------- 3. Метаданные (типы объектов) ----------
                var metadata = serverApi.GetMetadata(0);
                string metaFilePath = Path.Combine(desktopPath, $"PilotMetadata_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(metaFilePath, JsonSerializer.Serialize(metadata, jsonOptions), Encoding.UTF8);
                Console.WriteLine($"Метаданные сохранены: {metaFilePath} (типов: {metadata.Types.Count})");

                // ---------- 4. Сбор всех ID через журнал изменений ----------
                var allIds = new HashSet<Guid>();
                long pos = 0;

                Console.WriteLine("Сбор ID объектов через журнал изменений (GetChangesets)...");
                while (true)
                {
                    var changesets = serverApi.GetChangesets(pos, pos + ChangesetStep - 1);
                    if (changesets == null || changesets.Count == 0)
                        break;

                    foreach (var cs in changesets)
                    {
                        if (cs.Changed != null)
                        {
                            foreach (var id in cs.Changed)
                            {
                                if (id != Guid.Empty)
                                    allIds.Add(id);
                            }
                        }
                    }
                    pos += ChangesetStep;
                    Console.WriteLine($"  Позиция: {pos}, уникальных ID: {allIds.Count}");
                }
                Console.WriteLine($"Всего уникальных ID: {allIds.Count}");

                // ---------- 5. Загрузка объектов порциями ----------
                var idList = allIds.ToList();
                var allObjects = new List<DObject>();

                Console.WriteLine("Загрузка объектов...");
                for (int i = 0; i < idList.Count; i += LoadBatchSize)
                {
                    var batchIds = idList.Skip(i).Take(LoadBatchSize).ToArray();
                    var objectsBatch = serverApi.GetObjects(batchIds);
                    if (objectsBatch != null)
                        allObjects.AddRange(objectsBatch);

                    Console.WriteLine($"  Загружено: {allObjects.Count}/{idList.Count}");
                }

                // ---------- 6. Фильтрация по папке (если задана) ----------
                List<DObject> exportObjects;

                if (folderId != Guid.Empty)
                {
                    Console.WriteLine($"Фильтрация по папке {folderId}...");
                    var objById = allObjects.ToDictionary(o => o.Id);
                    var descendants = new List<DObject>();

                    foreach (var obj in allObjects)
                    {
                        if (obj.Id == folderId) continue;

                        // Проверка по типу ДО тяжёлого поиска родителя
                        if (!allowedTypeIds.Contains(obj.TypeId))
                            continue;

                        var current = obj.ParentId;
                        bool found = false;
                        int depth = 0;

                        while (current != Guid.Empty && depth < 100)
                        {
                            if (current == folderId)
                            {
                                found = true;
                                break;
                            }
                            if (!objById.TryGetValue(current, out var parent))
                                break;
                            current = parent.ParentId;
                            depth++;
                        }

                        if (found)
                            descendants.Add(obj);
                    }

                    exportObjects = descendants;
                    Console.WriteLine($"Объектов в папке (включая подпапки) нужных типов: {exportObjects.Count}");
                }
                else
                {
                    // Выгружаем все объекты, но только нужных типов
                    Console.WriteLine("Фильтрация по типам объектов...");
                    exportObjects = allObjects.Where(o => allowedTypeIds.Contains(o.TypeId)).ToList();
                    Console.WriteLine($"Выгружаю объекты нужных типов: {exportObjects.Count}");
                }



                // ---------- 7. Сохранение объектов в JSON (потоковая запись) ----------
                string objectsFilePath = Path.Combine(desktopPath, $"PilotObjects_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                using (var fileStream = new FileStream(objectsFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536))
                using (var jsonWriter = new Utf8JsonWriter(fileStream, new JsonWriterOptions { Indented = true }))
                {
                    jsonWriter.WriteStartArray();
                    for (int i = 0; i < exportObjects.Count; i++)
                    {
                        JsonSerializer.Serialize(jsonWriter, exportObjects[i], jsonOptions);
                        if ((i + 1) % 10000 == 0)
                            Console.WriteLine($"  Записано в JSON: {i + 1}/{exportObjects.Count}");
                    }
                    jsonWriter.WriteEndArray();
                    jsonWriter.Flush();
                }
                Console.WriteLine($"Объекты сохранены (JSON): {objectsFilePath}");


                // ---------- Подготовка справочников для CSV ----------
                var typeNames = metadata.Types.ToDictionary(t => t.Id, t => t.Name);

                // Карта: TypeId → имя служебного атрибута (название объекта)
                var typeToNameAttr = new Dictionary<int, string>();
                foreach (var t in metadata.Types)
                {
                    var serviceAttr = t.Attributes.FirstOrDefault(a => a.IsService);
                    if (serviceAttr != null)
                    {
                        typeToNameAttr[t.Id] = serviceAttr.Name;
                    }
                    else
                    {
                        var nameAttr = t.Attributes.FirstOrDefault(a =>
                            string.Equals(a.Name, "name", StringComparison.OrdinalIgnoreCase));
                        if (nameAttr != null)
                            typeToNameAttr[t.Id] = nameAttr.Name;
                    }
                }
                Console.WriteLine($"Построена карта типов: {typeToNameAttr.Count}");


                // ---------- 8. Выгрузка структуры в CSV (плоские поля) ----------
                string structurePath = Path.Combine(desktopPath, $"PilotObjects_Structure_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                using (var writer = new StreamWriter(structurePath, false, Encoding.UTF8))
                {
                    writer.WriteLine("Id;ParentId;TypeId;TypeName;Title;IsDeleted;CreatedUtc");

                    foreach (var obj in exportObjects)
                    {
                        var typeName = typeNames.TryGetValue(obj.TypeId, out var tn) ? tn : "unknown";
                        var title = GetTitle(obj, typeToNameAttr);
                        var created = (obj.Created == DateTime.MinValue) ? "" : obj.Created.ToString("yyyy-MM-ddTHH:mm:ss");


                        writer.WriteLine($"{obj.Id};{obj.ParentId};{obj.TypeId};{typeName};{EscapeCsv(title)};{obj.IsDeleted};{created}");
                    }
                }
                Console.WriteLine($"Структура сохранена (CSV): {structurePath}");


                // ---------- 9. Выгрузка атрибутов в CSV (объект-атрибут-значение) ----------
                string attribPath = Path.Combine(desktopPath, $"PilotObjects_Attributes_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                int rowCount = 0;
                using (var writer = new StreamWriter(attribPath, false, Encoding.UTF8))
                {
                    writer.WriteLine("ObjectId;AttributeName;AttributeValue");

                    foreach (var obj in exportObjects)
                    {
                        foreach (var kvp in obj.Attributes)
                        {
                            string attrName = kvp.Key;
                            var dValue = kvp.Value;

                            string valueStr = "";
                            if (dValue != null && dValue.Value != null)
                            {
                                var rawValue = dValue.Value;
                                if (rawValue != null)
                                {
                                    valueStr = rawValue switch
                                    {
                                        string s => s,
                                        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss"),
                                        long l => l.ToString(),
                                        int i => i.ToString(),
                                        bool b => b.ToString(),
                                        Guid g => g.ToString(),
                                        _ => rawValue.ToString() ?? ""
                                    };
                                }
                            }

                            writer.WriteLine($"{obj.Id};{EscapeCsv(attrName)};{EscapeCsv(valueStr)}");
                            rowCount++;
                        }

                        if (rowCount % 100000 == 0)
                            Console.WriteLine($"  Обработано строк атрибутов: {rowCount}");
                    }
                }
                Console.WriteLine($"Атрибуты сохранены (CSV): {attribPath} (строк: {rowCount})");


                Console.WriteLine("\nГотово. Нажмите любую клавишу для выхода...");
                Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ReadKey();
            }
        }

        // Статический метод для получения названия объекта
        private static string GetTitle(DObject obj, Dictionary<int, string> typeToNameAttr)
        {
            if (typeToNameAttr.TryGetValue(obj.TypeId, out var attrName) &&
                obj.Attributes.TryGetValue(attrName, out var value))
            {
                return value?.Value?.ToString() ?? "";
            }
            return "";
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }
    }
}
