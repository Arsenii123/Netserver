using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;

class Program
{
    static readonly ConcurrentDictionary<string, DateTime> Clients = new();

    // строка подключения к PostgreSQL
    static string ConnString = "Host=localhost;Username=postgres;Password=123;Database=chat";

    static async Task Main()
    {
        string port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
        string url = $"http://+:{port}/";

        var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        Console.WriteLine($"Сервер запущено на порту {port}");
        Console.WriteLine("Ендпоінти:");
        Console.WriteLine("  POST /connect              -> реєстрація клієнта");
        Console.WriteLine("  POST /disconnect           -> відключення клієнта");
        Console.WriteLine("  POST /send                 -> надіслати повідомлення");
        Console.WriteLine("  GET  /poll?clientId=&from= -> отримати нові повідомлення");
        Console.WriteLine();

        _ = Task.Run(CleanupLoop);

        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(() => HandleRequest(context));
        }
    }

    static async Task HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;
        res.ContentType = "application/json; charset=utf-8";

        try
        {
            string path = req.Url?.AbsolutePath ?? "/";

            if      (req.HttpMethod == "POST" && path == "/connect")    await HandleConnect(req, res);
            else if (req.HttpMethod == "POST" && path == "/disconnect") await HandleDisconnect(req, res);
            else if (req.HttpMethod == "POST" && path == "/send")       await HandleSend(req, res);
            else if (req.HttpMethod == "GET"  && path == "/poll")       await HandlePoll(req, res);
            else
            {
                res.StatusCode = 404;
                await WriteJson(res, new { error = "Не знайдено" });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Помилка: {ex.Message}");
            res.StatusCode = 500;
            await WriteJson(res, new { error = ex.Message });
        }
    }

    // POST /connect
    static async Task HandleConnect(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJson<ConnectRequest>(req);
        string id = body?.ClientId ?? Guid.NewGuid().ToString("N")[..8];

        Clients[id] = DateTime.UtcNow;

        string sysMsg = $"Клієнт {id} приєднався до чату";
        AddSystemMessage(sysMsg);
        Console.WriteLine($"[+] {sysMsg}. Усього клієнтів: {Clients.Count}");

        int fromIndex = GetMessageCount();
        await WriteJson(res, new { clientId = id, fromIndex });
    }

    // POST /disconnect
    static async Task HandleDisconnect(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJson<ConnectRequest>(req);
        if (body?.ClientId != null && Clients.TryRemove(body.ClientId, out _))
        {
            string sysMsg = $"Клієнт {body.ClientId} покинув чат";
            AddSystemMessage(sysMsg);
            Console.WriteLine($"[-] {sysMsg}. Усього клієнтів: {Clients.Count}");
        }
        await WriteJson(res, new { ok = true });
    }

    // POST /send
    static async Task HandleSend(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJson<SendRequest>(req);

        if (string.IsNullOrWhiteSpace(body?.ClientId) || string.IsNullOrWhiteSpace(body?.Text))
        {
            res.StatusCode = 400;
            await WriteJson(res, new { error = "clientId та text обов'язкові" });
            return;
        }

        if (!Clients.ContainsKey(body.ClientId))
        {
            res.StatusCode = 403;
            await WriteJson(res, new { error = "Спочатку підключіться через /connect" });
            return;
        }

        Clients[body.ClientId] = DateTime.UtcNow;
        AddMessage(body.ClientId, body.Text);
        Console.WriteLine($"[{body.ClientId}]: {body.Text}");

        await WriteJson(res, new { ok = true });
    }

    // GET /poll
    static async Task HandlePoll(HttpListenerRequest req, HttpListenerResponse res)
    {
        string? clientId = req.QueryString["clientId"];
        string? fromStr  = req.QueryString["from"];

        if (string.IsNullOrWhiteSpace(clientId) || !Clients.ContainsKey(clientId))
        {
            res.StatusCode = 403;
            await WriteJson(res, new { error = "Невідомий клієнт" });
            return;
        }

        Clients[clientId] = DateTime.UtcNow;
        int from = int.TryParse(fromStr, out int f) ? f : 0;

        var newMessages = LoadMessages(from);
        int nextFrom = from + newMessages.Count;

        await WriteJson(res, new { messages = newMessages, nextFrom });
    }

    static async Task CleanupLoop()
    {
        while (true)
        {
            await Task.Delay(10_000);
            var cutoff = DateTime.UtcNow.AddSeconds(-15);
            foreach (var (id, lastSeen) in Clients)
            {
                if (lastSeen < cutoff && Clients.TryRemove(id, out _))
                {
                    string sysMsg = $"Клієнт {id} відключився (таймаут)";
                    AddSystemMessage(sysMsg);
                    Console.WriteLine($"[~] {sysMsg}. Усього клієнтів: {Clients.Count}");
                }
            }
        }
    }

    // --- Работа с сообщениями через PostgreSQL ---
    static void AddMessage(string clientId, string text)
    {
        var msg = new ChatMessage { From = clientId, Text = text, IsSystem = false };
        SaveMessage(msg);
    }

    static void AddSystemMessage(string text)
    {
        var msg = new ChatMessage { From = "server", Text = text, IsSystem = true };
        SaveMessage(msg);
    }

    static void SaveMessage(ChatMessage msg)
    {
        using var con = new NpgsqlConnection(ConnString);
        con.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO messages (sender, text, isSystem) VALUES (@s, @t, @i)", con);
        cmd.Parameters.AddWithValue("s", msg.From);
        cmd.Parameters.AddWithValue("t", msg.Text);
        cmd.Parameters.AddWithValue("i", msg.IsSystem);
        cmd.ExecuteNonQuery();
    }

    static List<ChatMessage> LoadMessages(int from)
    {
        using var con = new NpgsqlConnection(ConnString);
        con.Open();
        using var cmd = new NpgsqlCommand("SELECT id, sender, text, isSystem FROM messages WHERE id > @f ORDER BY id", con);
        cmd.Parameters.AddWithValue("f", from);
        using var reader = cmd.ExecuteReader();

        var list = new List<ChatMessage>();
        while (reader.Read())
        {
            list.Add(new ChatMessage {
                From = reader.GetString(1),
                Text = reader.GetString(2),
                IsSystem = reader.GetBoolean(3)
            });
        }
        return list;
    }

    static int GetMessageCount()
    {
        using var con = new NpgsqlConnection(ConnString);
        con.Open();
        using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM messages", con);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // --- Вспомогательные методы ---
    static async Task WriteJson(HttpListenerResponse res, object obj)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    static async Task<T?> ReadJson<T>(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}

class ChatMessage
{
    public string From     { get; set; } = "";
    public string Text     { get; set; } = "";
    public bool   IsSystem { get; set; }
}

class ConnectRequest { public string? ClientId { get; set; } }
class SendRequest    { public string? ClientId { get; set; } public string? Text { get; set; } }



