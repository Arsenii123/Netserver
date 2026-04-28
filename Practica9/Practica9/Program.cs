namespace Practica9
{
    using System;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    class Program
    {
        static readonly HttpClient Http = new();
        static readonly string ServerUrl = "https://netserver-fp42.onrender.com";

        static string ClientId = "";
        static int NextFrom = 0;

        static async Task Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            await Connect();

            Console.WriteLine("Введіть повідомлення та натисніть Enter. Ctrl+C для виходу.\n");

            using var cts = new CancellationTokenSource();
            var pollTask = Task.Run(() => PollLoop(cts.Token));

            Console.CancelKeyPress += async (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                await Disconnect();
                Environment.Exit(0);
            };

            while (true)
            {
                string? line = Console.ReadLine();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                await SendMessage(line);
            }

            cts.Cancel();
            await Disconnect();
        }

        // POST /connect
        static async Task Connect()
        {
            while (true)
            {
                try
                {
                    Console.Write("Введіть ім'я: ");
                    string? name = Console.ReadLine();

                    var body = new { name = name };
                    var response = await PostJson("/connect", body);

                    if (response.TryGetProperty("clientId", out var cid))
                        ClientId = cid.GetString()!;
                    else
                        Console.WriteLine("Поле clientId отсутствует в ответе");

                    if (response.TryGetProperty("fromIndex", out var fi))
                        NextFrom = fi.GetInt32();
                    else
                        Console.WriteLine("Поле fromIndex отсутствует в ответе");

                    Console.WriteLine($"Підключено як {name}, ваш ID: {ClientId}");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Не вдалося підключитися: {ex.Message}");
                    Console.Write("Спробувати ще раз? (Enter = так, будь-що = ні): ");
                    var ans = Console.ReadLine();
                    if (ans != "") Environment.Exit(1);
                }
            }
        }

        // POST /disconnect
        static async Task Disconnect()
        {
            try
            {
                await PostJson("/disconnect", new { clientId = ClientId });
                Console.WriteLine("Відключено від сервера.");
            }
            catch { }
        }

        // POST /send
        static async Task SendMessage(string text)
        {
            try
            {
                await PostJson("/send", new { clientId = ClientId, text });
            }
            catch (Exception ex)
            {
                PrintSystem($"Помилка відправки: {ex.Message}");
            }
        }

        // GET /poll
        static async Task PollLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var url = $"{ServerUrl}/poll?clientId={ClientId}&from={NextFrom}";
                    var json = await Http.GetStringAsync(url, ct);

                    // тимчасово можна вивести сирий JSON для перевірки
                    // Console.WriteLine(json);

                    var doc = JsonDocument.Parse(json).RootElement;

                    if (doc.TryGetProperty("nextFrom", out var nf))
                        NextFrom = nf.GetInt32();
                    else
                        PrintSystem("Поле nextFrom отсутствует в ответе");

                    if (doc.TryGetProperty("messages", out var msgs))
                    {
                        foreach (var msg in msgs.EnumerateArray())
                        {
                            string from = msg.TryGetProperty("from", out var f) ? f.GetString()! : "unknown";
                            string text = msg.TryGetProperty("text", out var t) ? t.GetString()! : "";
                            bool isSystem = msg.TryGetProperty("isSystem", out var s) && s.GetBoolean();

                            if (isSystem)
                                PrintSystem(text);
                            else
                                PrintMessage(from, text);
                        }
                    }
                    else
                    {
                        PrintSystem("Поле messages отсутствует в ответе");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    PrintSystem($"Polling помилка: {ex.Message}");
                }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        // --- Вивід у консоль ---
        static void PrintMessage(string from, string text)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"[{from}]: ");
            Console.ForegroundColor = prev;
            Console.WriteLine(text);
        }

        static void PrintSystem(string text)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"*** {text}");
            Console.ForegroundColor = prev;
        }

        // --- HTTP хелпери ---
        static async Task<JsonElement> PostJson(string path, object body)
        {
            var content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var response = await Http.PostAsync(ServerUrl + path, content);
            var json = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(json).RootElement;
        }
    }
}


