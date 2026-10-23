using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using MagicSurvivalCoop.Net;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Кооператив Magic Survival на 2–4 игрока через виртуальную сеть (Anywherelan, Tailscale, ZeroTier).
    ///
    /// Как устроено: у каждого игрока свой мир игры (враги и прокачка свои — это ограничение игры, общий мир без
    /// исходников не синхронизировать). Друзья появляются в вашем мире союзниками-двойниками: двигаются так же,
    /// как настоящий друг двигается в своём мире, и стреляют его лучшим заклинанием. Над экраном — здоровье и
    /// убийства друзей, кнопка «Лечить друга», автоматический сигнал «нужна помощь».
    ///
    /// Сервер встроен в мод: хост нажимает «Создать игру», остальные вводят его IP из Anywherelan.
    /// </summary>
    public class CoopMod : IKitMod
    {
        public const string ModVersion = "1.4.8";

        public ModDef Define() => new ModDef
        {
            Id = "coop",
            Name = "Кооператив",
            Version = ModVersion,
            Description = "Игра с друзьями (2–4) через Anywherelan/Tailscale/ZeroTier: друзья — союзники в вашем бою, их здоровье на экране, лечение друга.",
            Depends = { ["modkit_combat"] = ">=1.0", ["modkit_hero"] = ">=1.0", ["modkit_spells"] = ">=1.0", ["modkit_companions"] = ">=1.0" },
            Options =
            {
                new ModOption { Key = "ghostDist", Label = "Дальность призрака друга", Default = 6, Min = 2, Max = 12, Step = 1 },
                new ModOption { Key = "healPct", Label = "Лечение друга, % (0 — выкл.)", Default = 25, Min = 0, Max = 50, Step = 5 },
                new ModOption { Key = "healCd", Label = "Перезарядка лечения, с", Default = 40, Min = 10, Max = 120, Step = 5 },
                new ModOption { Key = "mirrorDmg", Label = "Урон магии друга у вас, % (0 — только эффект)", Default = 100, Min = 0, Max = 200, Step = 25 },
                new ModOption { Key = "orbitals", Label = "Показывать Щит, Спутников и Духов друга (1 — да, 0 — нет)", Default = 1, Min = 0, Max = 1, Step = 1 },
                new ModOption { Key = "sendHz", Label = "Обновлений позиции в секунду (больше — плавнее, но больше трафика)", Default = 15, Min = 5, Max = 30, Step = 5 },
                new ModOption { Key = "ip1", Label = "IP 1", Default = 10, Min = 0, Max = 255, Hidden = true },
                new ModOption { Key = "ip2", Label = "IP 2", Default = 66, Min = 0, Max = 255, Hidden = true },
                new ModOption { Key = "ip3", Label = "IP 3", Default = 0, Min = 0, Max = 255, Hidden = true },
                new ModOption { Key = "ip4", Label = "IP 4", Default = 1, Min = 0, Max = 255, Hidden = true },
                new ModOption { Key = "port", Label = "Порт", Default = Protocol.DefaultPort, Min = 1024, Max = 65535, Hidden = true },
            },
            OnEnable = ctx => _session = new Session(ctx),
            OnDisable = ctx => { _session?.Shutdown(); _session = null; },
            OpenSettings = ctx => _session?.OpenWindow(),
        };

        private Session _session;
    }

    /// <summary>Всё состояние мода, пока он включён.</summary>
    internal sealed partial class Session
    {
        // заклинания, которыми умеет стрелять союзник (проверено в modkit_companions: 2, 5, 7; 16, 13, 1 — снаряды того же типа)
        private static readonly int[] GhostSpells = { 2, 5, 7, 16, 13, 1 };
        private static readonly double[][] SlotColors =
        {
            new[] { 1.0, 0.85, 0.30, 0.80 },   // 1 — золотой
            new[] { 0.45, 0.70, 1.00, 0.80 },  // 2 — голубой
            new[] { 0.45, 1.00, 0.55, 0.80 },  // 3 — зелёный
            new[] { 1.00, 0.50, 0.80, 0.80 },  // 4 — розовый
        };
        private static readonly double[] BtnBlue = { 0.20, 0.45, 0.85, 1 };
        private static readonly double[] BtnGreen = { 0.20, 0.60, 0.30, 1 };
        private static readonly double[] BtnRed = { 0.65, 0.22, 0.22, 1 };
        private static readonly double[] BtnGray = { 0.30, 0.30, 0.30, 1 };

        private readonly ModContext _ctx;
        private readonly IEnemies _enemies;
        private readonly IHero _hero;
        private readonly ISpells _spells;
        private readonly ICompanions _companions;

        private readonly CoopClient _client = new CoopClient();
        private CoopServer _server;
        private readonly ConcurrentQueue<string> _bgLog = new ConcurrentQueue<string>();

        private readonly Dictionary<string, Partner> _partners = new Dictionary<string, Partner>();
        private string _myId, _roomCode, _myName, _clientId;
        private int _mySlot;
        private bool _hosting;
        private int _hostPort;
        private string _lastError, _notice;        // показываются в окне «Кооп»: подписи-тосты в главном меню не видны

        // свой бой
        private bool _inRun;
        private int _kills;
        private double? _startX, _startY;
        private double _lastX, _lastY;
        private bool _wasDead;
        private double _healReadyAt, _nextSos;

        // таймеры главного потока (мс реального времени)
        private double _clockMs, _nextSend, _nextHud, _nextWin;
        private int _hudFails;

        // плавное движение союзников: каждый кадр через хук C_Battle_Manager.Update
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private double _lastFrameSec = -1, _frameSeenSec = -100, _fpsSince = -1;
        private int _fpsFrames;
        private bool _fpsLogged;
        private bool _directWrite = true;
        private int _writeFails;
        private Type _compType;
        private FieldInfo _compTr;
        private Transform _heroTr;                 // объект героя — один раз за бой (без мусора в каждом кадре)
        private bool _heroTrTried;
        private double _heroTrRetryAt;
        private Action _sendAction, _flushAction;
        private int _bestSpell;
        private readonly Dictionary<int, int> _levels = new Dictionary<int, int>();   // уровни заклинаний за бой — для итога в журнал
        private double _bestSpellAt = -1;
        private const int PortSpan = 4;            // если основной порт занят, хост берёт следующий (до +4), клиенты ищут его сами

        // интерфейс
        private UiKit.HudButton _menuBtn, _healBtn;
        private bool _windowOpen;
        private UnityEngine.UI.Text _winStatus, _winPlayers;

        private sealed class Partner
        {
            public string Id, Name;
            public int Slot;
            public bool HasState, Run, Battle, Paused, Dead;
            public double X, Y, Sx, Sy, Hp, HpMax, Kills;
            public int Spell;
            public double StateAt;           // _clockMs последнего состояния
            // сглаживание: последняя точка (смещение от старта друга), оценка скорости и то, что рисуется сейчас
            public double RelX, RelY, VelX, VelY, SampleSec = -1, DispX, DispY;
            public bool HasDisp;
            // интерполяция по времени отправителя: точки (время друга, x, y) и сдвиг его часов относительно наших
            public readonly List<(double t, double x, double y)> Track = new List<(double, double, double)>();
            public double ClockOff;
            public bool HasClock;
            public Transform GhostTr;          // объект союзника — берётся один раз, а не каждый кадр
            public bool Mirror;                // друг пересылает свою магию (1.3+): союзник не стреляет сам, повторяет касты друга
            public readonly Queue<Cast> Casts = new Queue<Cast>();
            public OrbState Orb;               // Щит/Спутники/Духи друга (1.4+)
            public OrbVisuals OrbVis;          // наши копии вокруг его союзника
            public ICompanion Ghost;
            public int GhostSpell = -1;
            public double NextSpawn;
            public UiKit.HudBar Bar;
        }

        public Session(ModContext ctx)
        {
            _ctx = ctx;
            _enemies = ctx.Use<IEnemies>("modkit_combat");
            _hero = ctx.Use<IHero>("modkit_hero");
            _spells = ctx.Use<ISpells>("modkit_spells");
            _companions = ctx.Use<ICompanions>("modkit_companions");

            _myName = ctx.Store.Get("name", "");
            if (string.IsNullOrWhiteSpace(_myName))
            {
                _myName = "Игрок " + new Random().Next(10, 100);
                ctx.Store.Set("name", _myName);
            }

            _clientId = ctx.Store.Get("clientId", "");
            if (string.IsNullOrEmpty(_clientId)) { _clientId = Guid.NewGuid().ToString("N"); ctx.Store.Set("clientId", _clientId); }
            _client.Log = s => _bgLog.Enqueue("клиент: " + s);

            ctx.Events.On("mainMenu", _ => ctx.Guard("кнопка в меню", ShowMenuButton));
            ctx.Events.On("battleStart", _ => ctx.Guard("начало боя", OnBattleStart));
            ctx.Events.On("battleEnd", _ => ctx.Guard("конец боя", OnBattleEnd));
            ctx.Events.On("enemyDeath", _ => { if (_inRun) _kills++; });

            _sendAction = SendState;
            _flushAction = FlushCasts;
            ctx.Every(50, Tick);
            InstallMagicHook();
            try { ctx.Before("C_Battle_Manager", "Update", 0, (self, args) => { if (self != null) _battleMgr = self; FrameStep(); }); }
            catch (Exception e) { ctx.Log("покадровый хук недоступен, союзники двигаются 20 раз в секунду: " + e.Message); }
            ctx.OnCleanup(Shutdown);
            ctx.Log($"кооператив {CoopMod.ModVersion} включён, имя «{_myName}»");
            ctx.Guard("кнопка в меню", ShowMenuButton);   // если мод включили прямо в главном меню
        }

        public void Shutdown()
        {
            try { _client.Stop(); } catch { }
            try { _server?.Stop(); } catch { }
            _server = null;
            foreach (var p in _partners.Values) DropPartnerVisuals(p);
            _partners.Clear();
            DestroyBtn(ref _menuBtn);
            DestroyBtn(ref _healBtn);
            _windowOpen = false;
        }

        // ============================================================ подключение

        private int Port => (int)_ctx.Setting("port");

        private string HostIp => string.Join(".", new[] { "ip1", "ip2", "ip3", "ip4" }.Select(k => ((int)_ctx.Setting(k)).ToString(CultureInfo.InvariantCulture)));

        private void Host()
        {
            Disconnect();
            _lastError = null; _notice = null;
            string firstError = null;
            for (int port = Port; port <= Math.Min(65535, Port + PortSpan); port++)
            {
                var srv = new CoopServer { Log = s => _bgLog.Enqueue("сервер: " + s) };
                try { srv.Start(port); }
                catch (Exception e)
                {
                    firstError ??= e.Message;
                    _ctx.Log($"порт {port} недоступен: {e.Message}");
                    continue;
                }
                _server = srv;
                _hostPort = port;
                break;
            }
            if (_server == null)
            {
                _lastError = $"Не удалось создать игру: порты {Port}–{Port + PortSpan} заняты другой программой на этом телефоне " +
                             $"(например, сервером в Termux). Закройте её или укажите другой порт.\n({firstError})";
                _ctx.Warn(_lastError);
                return;
            }
            _hosting = true;
            _client.Start("127.0.0.1", _hostPort, _myName, clientId: _clientId);
            _notice = _hostPort == Port
                ? "Игра создана. Сообщите друзьям IP, отмеченный «← VPN»."
                : $"Порт {Port} занят другой программой, игра создана на порту {_hostPort}. Друзья с портом {Port} найдут её сами.";
            _ctx.Log($"хост: сервер на порту {_hostPort}");
            _ctx.Toast(_notice, 4);
        }

        private void Join()
        {
            Disconnect();
            _lastError = null; _notice = null;
            _hosting = false;
            _client.Start(HostIp, Port, _myName, portSpan: PortSpan, clientId: _clientId);
            _ctx.Log($"подключение к {HostIp}:{Port}");
        }

        private void Disconnect()
        {
            _client.Stop();
            _server?.Stop();
            _server = null;
            _hosting = false;
            _myId = null; _roomCode = null; _mySlot = 0;
            foreach (var p in _partners.Values) DropPartnerVisuals(p);
            _partners.Clear();
        }

        // ============================================================ главный цикл

        private void Tick()
        {
            _clockMs += 50;
            while (_bgLog.TryDequeue(out var line)) _ctx.Log(line);

            _ctx.Guard("сеть", Pump);

            MaybeSend();

            _ctx.Guard("союзники", UpdateGhosts);

            if (_clockMs >= _nextHud) { _nextHud = _clockMs + 250; UpdateHud(); }
            if (_windowOpen && _clockMs >= _nextWin) { _nextWin = _clockMs + 500; UpdateWindow(); }
        }

        /// <summary>Отправка по реальному времени: из таймера (меню) и из каждого кадра боя — так частота не ограничена шагом таймера 50 мс.</summary>
        private void MaybeSend()
        {
            double now = Clock.Elapsed.TotalSeconds;
            if (now < _nextSend) return;
            double interval = 1.0 / Math.Max(1, _ctx.Setting("sendHz"));
            _nextSend = Math.Max(now, _nextSend + interval);                // ровный шаг без накопления сдвига
            if (_nextSend - now > interval) _nextSend = now + interval;
            _ctx.Guard("отправка состояния", _sendAction);
            _ctx.Guard("отправка магии", _flushAction);
        }

        private void Pump()
        {
            int n = 0;
            while (n++ < 200 && _client.Incoming.TryDequeue(out var m))
            {
                switch (Protocol.TypeOf(m))
                {
                    case Protocol.RoomCreated:
                    case Protocol.RoomJoined:
                        {
                            _roomCode = Protocol.Str(m, "roomCode");
                            _myId = Protocol.Str(m, "playerId", _myId);
                            _mySlot = (int)Protocol.Num(m, "slot", _mySlot);
                            _client.RememberRoom(_roomCode);
                            var seen = new HashSet<string>();
                            if (m.TryGetProperty("players", out var arr) && arr.ValueKind == JsonValueKind.Array)
                                foreach (var pl in arr.EnumerateArray())
                                {
                                    var id = Protocol.Str(pl, "playerId");
                                    if (id == null || id == _myId) continue;
                                    seen.Add(id);
                                    var p = GetPartner(id);
                                    p.Name = Protocol.Str(pl, "name", p.Name);
                                    p.Slot = (int)Protocol.Num(pl, "slot", p.Slot);
                                }
                            foreach (var gone in _partners.Keys.Where(k => !seen.Contains(k)).ToList()) RemovePartner(gone);
                            _ctx.Log($"в комнате {_roomCode}: слот {_mySlot}, друзей {_partners.Count}");
                            _ctx.Toast(_partners.Count == 0 ? "Подключено. Ждём друзей…" : $"Подключено! Друзей в игре: {_partners.Count}", 3);
                        }
                        break;

                    case Protocol.PlayerJoined:
                        {
                            var p = GetPartner(Protocol.Str(m, "playerId"));
                            p.Name = Protocol.Str(m, "name", p.Name);
                            p.Slot = (int)Protocol.Num(m, "slot", p.Slot);
                            _ctx.Toast($"{p.Name} присоединился", 3);
                            _nextSend = 0;  // сразу отправить ему своё состояние
                        }
                        break;

                    case Protocol.PlayerLeft:
                        {
                            var id = Protocol.Str(m, "playerId");
                            if (id != null && _partners.TryGetValue(id, out var p)) _ctx.Toast($"{p.Name} вышел", 3);
                            RemovePartner(id);
                        }
                        break;

                    case Protocol.RemotePlayerState:
                        ApplyState(GetPartner(Protocol.Str(m, "playerId")), m);
                        break;

                    case Protocol.RemoteEvent:
                        OnRemoteEvent(Protocol.Str(m, "playerId"), Protocol.Obj(m, "event"));
                        break;

                    case Protocol.Error:
                        {
                            var msg = Protocol.Str(m, "message", "ошибка сервера");
                            _ctx.Log("сервер: " + msg);
                            _ctx.Toast(msg, 4);
                            var code = Protocol.Str(m, "code");
                            if (code == "room_full" || code == "protocol_mismatch") _client.Stop();   // переподключение не поможет
                        }
                        break;

                    case "DISCONNECTED":
                        foreach (var p in _partners.Values) DropPartnerVisuals(p);
                        _partners.Clear();
                        if (_client.State != CoopClient.Status.Off) _ctx.Toast("Связь потеряна, переподключаюсь…", 3);
                        break;
                }
            }
        }

        private Partner GetPartner(string id)
        {
            id ??= "?";
            if (!_partners.TryGetValue(id, out var p))
            {
                p = new Partner { Id = id, Name = "Друг", Slot = _partners.Count + 2 };
                _partners[id] = p;
            }
            return p;
        }

        private void RemovePartner(string id)
        {
            if (id == null || !_partners.TryGetValue(id, out var p)) return;
            DropPartnerVisuals(p);
            _partners.Remove(id);
        }

        private void DropPartnerVisuals(Partner p)
        {
            try { if (p.Ghost != null && p.Ghost.Alive) p.Ghost.Remove(); } catch { }
            p.Ghost = null;
            try { p.Bar?.Destroy(); } catch { }
            p.Bar = null;
            try { DestroyOrbVisuals(p); } catch { }
        }

        private void ApplyState(Partner p, JsonElement m)
        {
            var s = Protocol.Obj(m, "state");
            if (s.ValueKind != JsonValueKind.Object) return;
            if (m.TryGetProperty("slot", out _)) p.Slot = (int)Protocol.Num(m, "slot", p.Slot);
            if (s.TryGetProperty("name", out _)) p.Name = Protocol.CleanName(Protocol.Str(s, "name", p.Name));
            bool wasDead = p.Dead, wasBattle = p.Battle;
            p.Run = Protocol.Bool(s, "run");
            p.Battle = Protocol.Bool(s, "battle");
            p.Paused = Protocol.Bool(s, "paused");
            p.Dead = Protocol.Bool(s, "dead");
            p.X = Protocol.Num(s, "x"); p.Y = Protocol.Num(s, "y");
            p.Sx = Protocol.Num(s, "sx", p.X); p.Sy = Protocol.Num(s, "sy", p.Y);
            p.Hp = Protocol.Num(s, "hp"); p.HpMax = Math.Max(1, Protocol.Num(s, "hpMax", 1));
            p.Kills = Protocol.Num(s, "kills");
            p.Spell = (int)Protocol.Num(s, "spell");
            if (Protocol.Bool(s, "mirror")) p.Mirror = true;
            ReadOrbits(p, s, Clock.Elapsed.TotalSeconds);
            p.HasState = true;
            p.StateAt = _clockMs;

            // точка для сглаживания: смещение друга от его старта; скорость — по двум последним точкам
            double now = Clock.Elapsed.TotalSeconds;
            double rx = p.X - p.Sx, ry = p.Y - p.Sy;
            double dt = now - p.SampleSec;
            bool newRun = !p.Battle || Math.Abs(rx - p.RelX) > 8 || Math.Abs(ry - p.RelY) > 8;   // новый бой или скачок
            if (p.SampleSec < 0 || newRun) { p.VelX = p.VelY = 0; if (newRun) p.HasDisp = false; }
            else if (dt > 0.02 && dt < 0.6)
            {
                double vx = (rx - p.RelX) / dt, vy = (ry - p.RelY) / dt;
                double sp = Math.Sqrt(vx * vx + vy * vy);
                if (sp > 15) { vx = vx / sp * 15; vy = vy / sp * 15; }
                p.VelX = p.VelX * 0.4 + vx * 0.6;
                p.VelY = p.VelY * 0.4 + vy * 0.6;
            }
            else if (dt >= 0.6) { p.VelX = p.VelY = 0; }
            p.RelX = rx; p.RelY = ry; p.SampleSec = now;

            // время отправителя (версия 1.2+): показываем друга с небольшой задержкой, интерполируя между его точками,
            // поэтому неравномерная доставка пакетов (мобильная сеть, VPN) не видна
            double st = Protocol.Num(s, "t", -1);
            if (st >= 0)
            {
                if (newRun || (p.Track.Count > 0 && st <= p.Track[p.Track.Count - 1].t - 5)) { p.Track.Clear(); p.HasClock = false; }   // новый бой / перезапуск игры у друга
                if (p.Track.Count == 0 || st > p.Track[p.Track.Count - 1].t)
                {
                    p.Track.Add((st, rx, ry));
                    if (p.Track.Count > 40) p.Track.RemoveAt(0);
                }
                // сдвиг часов = наименьшая задержка доставки (её разброс — это и есть «рывки»), медленно подстраивается вверх
                double off = now - st;
                if (!p.HasClock || off < p.ClockOff) { p.ClockOff = off; p.HasClock = true; }
                else p.ClockOff += (off - p.ClockOff) * 0.002;
            }
            if (p.Dead && !wasDead && p.Run) _ctx.Toast($"{p.Name} погиб!", 3);
            if (p.Battle && !wasBattle && !p.Dead && !_inRun) _ctx.Toast($"{p.Name} начал бой — заходите!", 3);
        }

        private void OnRemoteEvent(string from, JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            string who = from != null && _partners.TryGetValue(from, out var p) ? p.Name : "Друг";
            switch (Protocol.Str(e, "kind"))
            {
                case "heal":
                    {
                        if (!_inRun || _hero.Hp <= 0) break;
                        double pct = Math.Max(0, Math.Min(50, Protocol.Num(e, "pct", 25)));
                        double healed = _hero.Heal(_hero.HpMax * pct / 100.0);
                        _ctx.Toast($"{who} лечит вас: +{healed:0}", 3);
                    }
                    break;
                case "sos":
                    _ctx.Toast($"{who}: нужна помощь!", 3);
                    break;
                case "casts":
                    if (from != null && _partners.TryGetValue(from, out var cp)) OnRemoteCasts(cp, e);
                    break;
                case "arma":
                    OnRemoteArmageddon(who);
                    break;
            }
        }

        // ============================================================ свой бой

        private void OnBattleStart()
        {
            _inRun = true;
            _kills = 0;
            _startX = null; _startY = null;
            _wasDead = false;
            _healReadyAt = 0; _nextSos = 0;
            _fpsSince = -1; _fpsFrames = 0; _fpsLogged = false;
            _heroTr = null; _heroTrTried = false; _bestSpellAt = -1;
            ResetMagic();
            ResetArmageddon();
            foreach (var p in _partners.Values) p.GhostTr = null;
            foreach (var p in _partners.Values) { p.Ghost = null; p.NextSpawn = 0; p.GhostSpell = -1; }
            DestroyBtn(ref _menuBtn);
            _nextSend = 0;
        }

        private void OnBattleEnd()
        {
            _ctx.Guard("итог магии", LogMagicSummary);
            StopArmaReplay();
            _inRun = false;
            foreach (var p in _partners.Values) DropPartnerVisuals(p);
            DestroyBtn(ref _healBtn);
            _nextSend = 0;
        }

        private void SendState()
        {
            if (!_client.Connected) return;
            bool battle = _spells.InBattle;
            double hp = 0, hpMax = 1;
            try { hp = _hero.Hp; hpMax = Math.Max(1, _hero.HpMax); } catch { }
            bool dead = _inRun && hp <= 0;

            if (battle)
            {
                var pos = _enemies.HeroPos;
                if (pos.HasValue)
                {
                    _lastX = pos.Value.x; _lastY = pos.Value.y;
                    if (_startX == null) { _startX = _lastX; _startY = _lastY; }
                }
            }

            // лучшее «стреляющее» заклинание — им будет бить ваш призрак у друзей
            double nowS = Clock.Elapsed.TotalSeconds;
            if (!battle) _bestSpell = 0;
            else if (nowS - _bestSpellAt > 1)               // уровни заклинаний меняются редко — раз в секунду достаточно
            {
                _bestSpellAt = nowS;
                int best = 0; _bestSpell = 0;
                foreach (var id in GhostSpells)
                {
                    int lvl = _spells.Level(id);
                    if (lvl > best) { best = lvl; _bestSpell = id; }
                }
                for (int id = 1; id <= 21; id++) { int l = _spells.Level(id); if (l > 0) _levels[id] = l; }
            }
            int spell = _bestSpell;

            if (dead && !_wasDead) _ctx.Log("герой погиб");
            _wasDead = dead;

            // сигнал «нужна помощь»: мало здоровья, не чаще раза в 20 с игрового времени
            if (battle && !dead && hp / hpMax < 0.25 && _ctx.Timer.Now >= _nextSos && _partners.Count > 0)
            {
                _nextSos = _ctx.Timer.Now + 20;
                _client.SendEvent(Protocol.Build(w => w.WriteString("kind", "sos")));
            }

            if (_inRun) RefreshMyOrbits();
            _client.SendState(Protocol.Build(w =>
            {
                w.WriteString("name", _myName);
                w.WriteNumber("run", _inRun ? 1 : 0);
                w.WriteNumber("battle", battle ? 1 : 0);
                w.WriteNumber("paused", _spells.Paused ? 1 : 0);
                w.WriteNumber("dead", dead ? 1 : 0);
                w.WriteNumber("x", Math.Round(_lastX, 3));
                w.WriteNumber("y", Math.Round(_lastY, 3));
                w.WriteNumber("sx", Math.Round(_startX ?? _lastX, 3));
                w.WriteNumber("sy", Math.Round(_startY ?? _lastY, 3));
                w.WriteNumber("hp", Math.Round(hp));
                w.WriteNumber("hpMax", Math.Round(hpMax));
                w.WriteNumber("kills", _kills);
                w.WriteNumber("spell", spell);
                if (_magicHooked) w.WriteNumber("mirror", 1);
                if (_inRun) WriteMyOrbits(w);                    // мы пересылаем свою магию — пусть наш союзник у друзей не стреляет сам
                w.WriteNumber("t", Math.Round(Clock.Elapsed.TotalSeconds, 4));   // время снятия позиции — для интерполяции у друзей
            }));
        }

        // ============================================================ союзники-призраки

        private void UpdateGhosts()
        {
            bool battle = _spells.InBattle;
            foreach (var p in _partners.Values)
            {
                bool fresh = p.HasState && _clockMs - p.StateAt < 3000;
                bool want = battle && fresh && p.Battle && !p.Dead;
                if (!want)
                {
                    if (p.Ghost != null) { try { if (p.Ghost.Alive) p.Ghost.Remove(); } catch { } p.Ghost = null; }
                    continue;
                }

                if (p.Ghost == null || !p.Ghost.Alive)
                {
                    if (_clockMs < p.NextSpawn) continue;
                    p.NextSpawn = _clockMs + 2000;
                    var color = SlotColors[Math.Max(0, Math.Min(3, p.Slot - 1))];
                    _ctx.Mark("союзник: создание");
                    int gs = p.Mirror ? NoOwnFire : p.Spell;
                    p.Ghost = _companions.Spawn(0, 0, gs, color, 0.75, CompanionMode.Follow);
                    p.GhostSpell = gs;
                    p.GhostTr = null;
                    if (p.Ghost == null) continue;
                    p.Ghost.Speed = 200;   // библиотека сразу ставит союзника в нашу сглаженную точку, плавность — за нами
                    _ctx.Log($"союзник «{p.Name}» создан, слот {p.Ghost.Slot}, заклинание {p.Spell}");
                }

                int wantSpell = p.Mirror ? NoOwnFire : p.Spell;
                if (p.GhostSpell != wantSpell) { p.Ghost.Spell = wantSpell; p.GhostSpell = wantSpell; }

                // нет покадрового хука (или пауза кадров) — двигаем здесь, 20 раз в секунду
                if (Clock.Elapsed.TotalSeconds - _frameSeenSec > 0.25)
                {
                    var hp = _enemies.HeroPos;
                    if (hp.HasValue) PlaceGhost(p, 0.05, hp.Value.x, hp.Value.y, hp.Value.z, false);
                }
            }
        }

        /// <summary>Каждый кадр боя (хук C_Battle_Manager.Update): плавно ведём союзников.</summary>
        private void FrameStep()
        {
            double frameStart = Clock.Elapsed.TotalMilliseconds;
            try
            {
                double now = Clock.Elapsed.TotalSeconds;
                double dt = _lastFrameSec < 0 ? 0.016 : Math.Min(0.1, Math.Max(0, now - _lastFrameSec));
                _lastFrameSec = now;
                if (_inRun && !_fpsLogged)                        // одна строка в журнал: работает ли покадровое движение на этом телефоне
                {
                    if (_fpsSince < 0) _fpsSince = now;
                    _fpsFrames++;
                    if (now - _fpsSince >= 5) { _fpsLogged = true; _ctx.Log($"покадровое движение работает: {_fpsFrames / (now - _fpsSince):0} кадров/с"); }
                }
                if (_inRun && _client.Connected) MaybeSend();
                if (_inRun) ArmageddonStep(now);
                if (!_inRun || _partners.Count == 0 || _spells.Paused) return;
                if (!HeroPosition(out var hx, out var hy, out var hz)) return;
                _frameSeenSec = now;
                BeginFrameCasts();
                foreach (var p in _partners.Values)
                    if (p.Ghost != null && p.Ghost.Alive)
                    {
                        PlaceGhost(p, dt, hx, hy, hz, _directWrite);
                        if (p.Casts.Count > 0) { if (p.GhostTr == null) EnsureGhostTr(p); RunCasts(p, now, hz); }
                        if (p.Orb != null) { if (p.GhostTr == null) EnsureGhostTr(p); PlaceOrbits(p, now); }
                    }
                    else if (p.OrbVis != null) HideAll(p.OrbVis);
            }
            catch (Exception e)
            {
                if (++_writeFails == 1 || _writeFails % 500 == 0) _ctx.Log("кадр: " + e.Message);
            }
            if (_inRun) EndFrame(Clock.Elapsed.TotalMilliseconds - frameStart);
        }

        /// <summary>Позиция героя: объект героя берётся один раз за бой, дальше — одно чтение position без лишних объектов.</summary>
        private bool HeroPosition(out double x, out double y, out double z)
        {
            x = y = z = 0;
            double nowS = Clock.Elapsed.TotalSeconds;
            if (_heroTr == null && (!_heroTrTried || nowS >= _heroTrRetryAt))
            {
                _heroTrTried = true;
                _heroTrRetryAt = nowS + 1;                 // не нашли — следующая попытка не раньше чем через секунду
                try
                {
                    var hero = RawHero();
                    _heroTr = hero?.GetType().GetProperty("Transform_ObjUnit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(hero) as Transform;
                }
                catch { _heroTr = null; }
            }
            if (_heroTr != null)
            {
                try { var v = _heroTr.position; x = v.x; y = v.y; z = v.z; return true; }
                catch { _heroTr = null; }
            }
            var hp = _enemies.HeroPos;                     // запасной путь через библиотеку
            if (!hp.HasValue) return false;
            x = hp.Value.x; y = hp.Value.y; z = hp.Value.z;
            return true;
        }

        /// <summary>
        /// Сглаженная точка союзника: последняя точка друга + скорость × время с неё (не больше 0,25 с), к ней — плавно.
        /// Мир друга совмещён с нашим по точкам старта: где друг относительно своего старта, там союзник относительно нашего.
        /// </summary>
        private void PlaceGhost(Partner p, double dt, double heroX, double heroY, double heroZ, bool writeTransform)
        {
            double now = Clock.Elapsed.TotalSeconds;
            double tx, ty, rate;
            if (!Interpolate(p, now, out tx, out ty))
            {
                // друг на старой версии (нет времени отправителя) — предсказание по скорости
                double ahead = p.SampleSec < 0 ? 0 : Math.Min(0.25, Math.Max(0, now - p.SampleSec));
                tx = p.RelX + p.VelX * ahead; ty = p.RelY + p.VelY * ahead;
                rate = 12;
            }
            else rate = 30;   // интерполяция уже ровная — лишь сглаживаем поправки
            if (!p.HasDisp) { p.DispX = tx; p.DispY = ty; p.HasDisp = true; }
            else
            {
                double k = 1 - Math.Exp(-dt * rate);
                p.DispX += (tx - p.DispX) * k;
                p.DispY += (ty - p.DispY) * k;
            }

            double maxDist = _ctx.Setting("ghostDist");
            double ox = (_startX ?? heroX) + p.DispX - heroX;
            double oy = (_startY ?? heroY) + p.DispY - heroY;
            double len = Math.Sqrt(ox * ox + oy * oy);
            if (len > maxDist) { ox = ox / len * maxDist; oy = oy / len * maxDist; }
            else if (len < 0.8)                                // не прятаться под героем: отодвигаем по тому же направлению
            {
                if (len < 1e-3) { ox = 0.8; oy = 0; } else { ox = ox / len * 0.8; oy = oy / len * 0.8; }
            }
            p.Ghost.Offset = new Vector2((float)ox, (float)oy);
            if (writeTransform) WriteGhostTransform(p, heroX + ox, heroY + oy, heroZ);
        }

        /// <summary>
        /// Положение друга на момент «сейчас минус задержка» по его же часам: между двумя соседними точками — линейно,
        /// после последней — продолжаем с последней скоростью не дольше 0,2 с. Задержка — полтора средних интервала между точками.
        /// </summary>
        private static bool Interpolate(Partner p, double now, out double x, out double y)
        {
            x = y = 0;
            var tr = p.Track;
            if (!p.HasClock || tr.Count < 2) return false;
            double rt = now - p.ClockOff - RenderDelay(p);   // момент на часах друга, который показываем

            if (rt <= tr[0].t) { x = tr[0].x; y = tr[0].y; return true; }
            var last = tr[tr.Count - 1];
            if (rt >= last.t)
            {
                var prev = tr[tr.Count - 2];
                double dtl = last.t - prev.t;
                double ex = Math.Min(rt - last.t, 0.2);
                if (dtl <= 1e-4) { x = last.x; y = last.y; return true; }
                x = last.x + (last.x - prev.x) / dtl * ex;
                y = last.y + (last.y - prev.y) / dtl * ex;
                return true;
            }
            for (int i = tr.Count - 1; i > 0; i--)
            {
                var a = tr[i - 1]; var b = tr[i];
                if (rt < a.t) continue;
                double k = (rt - a.t) / Math.Max(1e-4, b.t - a.t);
                x = a.x + (b.x - a.x) * k;
                y = a.y + (b.y - a.y) * k;
                return true;
            }
            x = last.x; y = last.y;
            return true;
        }

        /// <summary>Задержка показа друга: полтора средних интервала между его точками (те же часы — и для движения, и для его магии).</summary>
        private static double RenderDelay(Partner p)
        {
            var tr = p.Track;
            if (tr.Count < 2) return 0.15;
            double span = tr[tr.Count - 1].t - tr[Math.Max(0, tr.Count - 11)].t;
            int gaps = Math.Min(10, tr.Count - 1);
            double interval = gaps > 0 ? span / gaps : 0.1;
            return Math.Max(0.06, Math.Min(0.35, interval * 1.5 + 0.02));
        }

        private void EnsureGhostTr(Partner p)
        {
            try
            {
                var t = p.Ghost.GetType();
                if (t != _compType) { _compType = t; _compTr = t.GetField("Tr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
                p.GhostTr = _compTr?.GetValue(p.Ghost) as Transform;
            }
            catch { p.GhostTr = null; }
        }

        /// <summary>Ставит объект союзника сразу (библиотека сама двигает его только 20 раз в секунду).</summary>
        private void WriteGhostTransform(Partner p, double x, double y, double z)
        {
            try
            {
                if (p.GhostTr == null)
                {
                    var t = p.Ghost.GetType();
                    if (t != _compType) { _compType = t; _compTr = t.GetField("Tr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
                    if (_compTr == null) return;
                    p.GhostTr = _compTr.GetValue(p.Ghost) as Transform;
                    if (p.GhostTr == null) return;
                }
                // библиотека сама подтягивает внутреннюю точку к герой+Offset (скорость 200), поэтому пишем только объект
                p.GhostTr.position = new Vector3((float)x, (float)y, (float)z);
            }
            catch (Exception e)
            {
                if (++_writeFails > 20) { _directWrite = false; _ctx.Log("прямое размещение союзника выключено: " + e.Message); }
            }
        }

        // ============================================================ HUD боя

        private void UpdateHud()
        {
            bool battle = _spells.InBattle;
            try
            {
                if (_menuBtn != null) _menuBtn.SetVisible(!battle && !_inRun);

                int i = 0;
                foreach (var p in _partners.Values.OrderBy(x => x.Slot))
                {
                    if (!battle) { if (p.Bar != null) { try { p.Bar.Destroy(); } catch { } p.Bar = null; } continue; }
                    if (p.Bar == null || !p.Bar.Alive)
                        p.Bar = UiKit.AddHudBar("coop_" + p.Id, 10, 150 + i * 36, 300, 30, SlotColors[Math.Max(0, Math.Min(3, p.Slot - 1))]);
                    p.Bar.Set(p.HasState ? p.Hp / p.HpMax : 0, PartnerLine(p));
                    i++;
                }

                UpdateHealButton(battle);
            }
            catch (Exception e)
            {
                // элементы могли исчезнуть вместе со сценой — пересоздадим на следующем тике
                if (++_hudFails == 1 || _hudFails % 200 == 0) _ctx.Log($"HUD: {e.Message} (раз: {_hudFails})");
                foreach (var p in _partners.Values) p.Bar = null;
                _healBtn = null;
            }
        }

        private string PartnerLine(Partner p)
        {
            if (!p.HasState || _clockMs - p.StateAt > 3000) return $"{p.Name}: нет связи";
            if (!p.Run) return $"{p.Name}: в меню";
            if (p.Dead) return $"{p.Name}: погиб · {p.Kills:0} убийств";
            string where = p.Paused ? " · пауза" : "";
            return $"{p.Name} · {p.Hp:0}/{p.HpMax:0} · {p.Kills:0} уб.{where}";
        }

        private void UpdateHealButton(bool battle)
        {
            double pct = _ctx.Setting("healPct");
            var target = HealTarget();
            bool show = battle && pct > 0 && target != null;
            if (!show) { if (_healBtn != null) _healBtn.SetVisible(false); return; }
            if (_healBtn == null || !_healBtn.Alive)
                _healBtn = UiKit.AddHudButton("coop_heal", "Лечить", 540, 330, 170, 64, 24, BtnGreen, () => _ctx.Guard("лечение друга", HealPressed));
            _healBtn.SetVisible(true);
            double left = _healReadyAt - _ctx.Timer.Now;
            _healBtn.SetText(left > 0 ? $"Лечить {Math.Ceiling(left):0}" : $"Лечить: {target.Name}");
        }

        private Partner HealTarget() => _partners.Values
            .Where(p => p.HasState && _clockMs - p.StateAt < 3000 && p.Battle && !p.Dead && p.Hp / p.HpMax < 0.7)
            .OrderBy(p => p.Hp / p.HpMax)
            .FirstOrDefault();

        private void HealPressed()
        {
            if (_ctx.Timer.Now < _healReadyAt) return;
            var t = HealTarget();
            if (t == null || !_client.Connected) return;
            double pct = _ctx.Setting("healPct");
            _client.SendEvent(Protocol.Build(w => { w.WriteString("kind", "heal"); w.WriteString("to", t.Id); w.WriteNumber("pct", pct); }));
            _healReadyAt = _ctx.Timer.Now + _ctx.Setting("healCd");
            _ctx.Toast($"Лечение отправлено: {t.Name}", 2);
        }

        // ============================================================ главное меню и окно

        private void ShowMenuButton()
        {
            if (_spells.InBattle || _inRun) return;
            DestroyBtn(ref _menuBtn);
            _menuBtn = UiKit.AddHudButton("coop_menu", "Кооп", 560, 110, 150, 60, 26, BtnBlue, () => _ctx.Guard("окно", OpenWindow));
        }

        private static void DestroyBtn(ref UiKit.HudButton b)
        {
            try { b?.Destroy(); } catch { }
            b = null;
        }

        public void OpenWindow()
        {
            var win = UiKit.OpenPage("coop", "Кооператив", "2–4 игрока через Anywherelan / Tailscale / ZeroTier", () => { _windowOpen = false; _winStatus = null; _winPlayers = null; });
            _windowOpen = true;
            var root = win.rt;
            double y = win.top + 10;

            _winStatus = UiKit.Label(root, StatusText(), 30, y, 660, 110, 24, 0);
            y += 120;

            UiKit.Label(root, "Ваш IP (скажите его друзьям, если вы хост):", 30, y, 660, 34, 22, 3);
            y += 36;
            UiKit.Label(root, LocalAddresses(), 30, y, 660, 70, 24, 0);
            y += 80;

            UiKit.AddButton(root, "Создать игру (я хост)", 30, y, 660, 76, 28, BtnGreen, () => SafeAction("Создать игру", Host));
            y += 100;

            UiKit.Label(root, "Подключиться к хосту — IP и порт:", 30, y, 660, 34, 22, 3);
            y += 40;
            string[] keys = { "ip1", "ip2", "ip3", "ip4" };
            for (int i = 0; i < 4; i++)
            {
                string key = keys[i];
                UiKit.AddNumberField(root, ((int)_ctx.Setting(key)).ToString(CultureInfo.InvariantCulture), 30 + i * 120, y, 100, 64, 28, false,
                    s => _ctx.Guard("ввод IP", () => { if (int.TryParse(s, out var v)) _ctx.SetSetting(key, Math.Max(0, Math.Min(255, v))); }));
                if (i < 3) UiKit.Label(root, ".", 132 + i * 120, y, 16, 64, 30, 4);
            }
            UiKit.Label(root, ":", 512, y, 16, 64, 30, 4);
            UiKit.AddNumberField(root, Port.ToString(CultureInfo.InvariantCulture), 530, y, 160, 64, 28, false,
                s => _ctx.Guard("ввод порта", () => { if (int.TryParse(s, out var v) && v >= 1024 && v <= 65535) _ctx.SetSetting("port", v); }));
            y += 84;

            UiKit.AddButton(root, "Подключиться", 30, y, 420, 76, 28, BtnBlue, () => SafeAction("Подключиться", Join));
            UiKit.AddButton(root, "Отключиться", 470, y, 220, 76, 26, _client.State == CoopClient.Status.Off ? BtnGray : BtnRed,
                () => SafeAction("Отключиться", () => { Disconnect(); _lastError = null; _notice = null; }));
            y += 100;

            UiKit.Label(root, "Игроки:", 30, y, 660, 34, 24, 3);
            y += 38;
            _winPlayers = UiKit.Label(root, PlayersText(), 30, y, 660, 200, 24, 0);
            y += 210;

            UiKit.Label(root, "У каждого свой мир игры: друзья приходят к вам союзниками и стреляют своим лучшим заклинанием. " +
                              "Кнопка «Лечить» в бою появляется, когда другу плохо.", 30, y, 660, 110, 20, 0);
        }

        /// <summary>Кнопка окна: ошибка не теряется молча, а показывается в окне и пишется в журнал; окно перерисовывается всегда.</summary>
        private void SafeAction(string what, Action fn)
        {
            try { fn(); }
            catch (Exception e)
            {
                _lastError = $"{what}: ошибка — {e.Message}";
                _ctx.Log($"ОШИБКА {what}: {e}");
            }
            try { OpenWindow(); } catch (Exception e) { _ctx.Log("окно: " + e.Message); }
        }

        private void UpdateWindow()
        {
            try
            {
                if (_winStatus != null) _winStatus.text = StatusText();
                if (_winPlayers != null) _winPlayers.text = PlayersText();
            }
            catch { _windowOpen = false; _winStatus = null; _winPlayers = null; }
        }

        private string StatusText()
        {
            if (_lastError != null) return _lastError;
            string role = _hosting ? $"Вы хост (порт {_server?.Port ?? Port})" : _client.State == CoopClient.Status.Off ? "Не подключено" : $"Хост {_client.Host}:{_client.Port}";
            string st = _client.State switch
            {
                CoopClient.Status.Connected => $"на связи, пинг {(_client.PingMs < 0 ? "—" : _client.PingMs + " мс")}",
                CoopClient.Status.Connecting => "подключаюсь…",
                CoopClient.Status.Waiting => "нет связи, повтор через пару секунд" + (_client.LastError != null ? $"\n{_client.LastError}" : ""),
                _ => "",
            };
            var text = st.Length == 0 ? role : $"{role}: {st}";
            return _notice != null && _client.State != CoopClient.Status.Off ? text + "\n" + _notice : text;
        }

        private string PlayersText()
        {
            if (_client.State != CoopClient.Status.Connected) return "—";
            var lines = new List<string> { $"{_mySlot}. {_myName} (вы)" + (_roomCode != null ? $" · комната {_roomCode}" : "") };
            foreach (var p in _partners.Values.OrderBy(x => x.Slot))
                lines.Add($"{p.Slot}. {PartnerLine(p)}");
            if (_partners.Count == 0) lines.Add("Ждём друзей…");
            return string.Join("\n", lines);
        }

        /// <summary>IPv4-адреса телефона; адреса виртуальных сетей (Anywherelan, Tailscale, ZeroTier) идут первыми.</summary>
        private static string LocalAddresses()
        {
            try
            {
                var list = new List<(string ip, string name, int rank)>();
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Down) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                        var b = a.GetAddressBytes();
                        bool vpn = ni.Name.StartsWith("tun") || ni.Name.StartsWith("awl") || ni.Name.StartsWith("zt") || ni.Name.StartsWith("tailscale")
                                   || (b[0] == 100 && b[1] >= 64 && b[1] < 128);
                        list.Add((a.ToString(), ni.Name, vpn ? 0 : 1));
                    }
                }
                if (list.Count == 0) return "не найдено — посмотрите IP в приложении Anywherelan";
                return string.Join("\n", list.OrderBy(x => x.rank).Take(3).Select(x => x.rank == 0 ? $"{x.ip}  ← VPN ({x.name})" : $"{x.ip}  ({x.name})"));
            }
            catch
            {
                return "не удалось определить — посмотрите IP в приложении Anywherelan";
            }
        }
    }
}
