using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MagicSurvivalCoop.Net
{
    /// <summary>
    /// Протокол v2: TCP, UTF-8, один JSON-объект на строку. Надмножество v1 из прототипа
    /// (CREATE_ROOM / JOIN_ROOM / PLAYER_STATE / PING / LEAVE_ROOM), добавлены EVENT/REMOTE_EVENT и слоты игроков.
    /// JSON пишется вручную через Utf8JsonWriter: без рефлексии, чтобы работало в любом рантайме.
    /// </summary>
    public static class Protocol
    {
        public const int Version = 2;
        public const int DefaultPort = 8766;
        public const int MaxPlayers = 4;
        public const int MaxLineBytes = 16 * 1024;

        // клиент → сервер
        public const string CreateRoom = "CREATE_ROOM";
        public const string JoinRoom = "JOIN_ROOM";
        public const string PlayerState = "PLAYER_STATE";
        public const string Event = "EVENT";
        public const string Ping = "PING";
        public const string LeaveRoom = "LEAVE_ROOM";

        // сервер → клиент
        public const string Welcome = "WELCOME";
        public const string RoomCreated = "ROOM_CREATED";
        public const string RoomJoined = "ROOM_JOINED";
        public const string PlayerJoined = "PLAYER_JOINED";
        public const string PlayerLeft = "PLAYER_LEFT";
        public const string RemotePlayerState = "REMOTE_PLAYER_STATE";
        public const string RemoteEvent = "REMOTE_EVENT";
        public const string Pong = "PONG";
        public const string Error = "ERROR";

        /// <summary>Собрать одну строку JSON (без перевода строки).</summary>
        public static string Build(Action<Utf8JsonWriter> body)
        {
            var buf = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buf))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buf.WrittenSpan);
        }

        public static string Simple(string type) => Build(w => w.WriteString("type", type));

        public static string TypeOf(JsonElement e) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        public static string Str(JsonElement e, string key, string def = null) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : def;

        public static double Num(JsonElement e, string key, double def = 0)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return def;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) return d;
            if (v.ValueKind == JsonValueKind.True) return 1;
            if (v.ValueKind == JsonValueKind.False) return 0;
            return def;
        }

        public static bool Bool(JsonElement e, string key) => Num(e, key, 0) != 0;

        public static JsonElement Obj(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

        /// <summary>Чистит имя игрока: без управляющих символов, до 20 знаков.</summary>
        public static string CleanName(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s ?? "")
            {
                if (char.IsControl(ch)) continue;
                sb.Append(ch);
                if (sb.Length >= 20) break;
            }
            var r = sb.ToString().Trim();
            return r.Length == 0 ? "Игрок" : r;
        }
    }

    /// <summary>Чтение строк с ограничением длины (защита от «бесконечной строки»).</summary>
    public sealed class LineReader
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[4096];
        private int _pos, _len;
        private readonly MemoryStream _line = new MemoryStream();

        public LineReader(Stream s) { _s = s; }

        /// <summary>Следующая строка или null, если соединение закрыто. Слишком длинная строка — IOException.</summary>
        public string ReadLine()
        {
            _line.SetLength(0);
            while (true)
            {
                if (_pos >= _len)
                {
                    _len = _s.Read(_buf, 0, _buf.Length);
                    _pos = 0;
                    if (_len <= 0) return _line.Length > 0 ? Encoding.UTF8.GetString(_line.ToArray()) : null;
                }
                int start = _pos;
                while (_pos < _len && _buf[_pos] != (byte)'\n') _pos++;
                _line.Write(_buf, start, _pos - start);
                if (_line.Length > Protocol.MaxLineBytes) throw new IOException("строка длиннее " + Protocol.MaxLineBytes + " байт");
                if (_pos < _len) { _pos++; return Encoding.UTF8.GetString(_line.ToArray()).TrimEnd('\r'); }
            }
        }
    }
}
