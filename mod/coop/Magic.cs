using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MagicSurvivalCoop.Net;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Магия друга: каждый снаряд, который создаёт герой (C_Battle_AllyUnit.Bullet_Create — через него идут Огненный шар,
    /// Метеорит, Молния и большинство других заклинаний), пересылается друзьям и повторяется у них из точки, где стоит союзник-друг:
    /// тот же тип снаряда, те же параметры, цель — на том же месте относительно союзника (или ближайший враг рядом с ней).
    /// Повтор идёт с той же задержкой, что и движение союзника, поэтому каст совпадает с его положением.
    /// Урон повтора считает игра по характеристикам героя получателя (множитель — настройка «Урон магии друга, %»).
    /// </summary>
    internal sealed partial class Session
    {
        /// <summary>Заклинание союзника, которое ничего не стреляет сам: при пересылке магии стреляют только повторы касов друга.</summary>
        private const int NoOwnFire = 99;
        private const int MaxCastsPerSend = 40;
        private const int MaxCastsPerFrame = 4;    // остальные — в следующих кадрах (повтор снаряда — работа самой игры, её надо дозировать)
        private const double CastBudgetMs = 3;

        private float[] _enemyX = new float[64], _enemyY = new float[64];
        private int _enemyCount;
        private double _enemyCacheAt = -1;
        private readonly System.Diagnostics.Stopwatch _frameCastWatch = new System.Diagnostics.Stopwatch();

        /// <summary>Начало кадра: обнулить бюджет повторов (вызывается из FrameStep).</summary>
        private void BeginFrameCasts() => _frameCastWatch.Restart();

        /// <summary>Позиции живых врагов — не чаще 4 раз в секунду и только когда есть что повторять.</summary>
        private void RefreshEnemyCache(double now)
        {
            if (now - _enemyCacheAt < 0.25) return;
            _enemyCacheAt = now;
            _enemyCount = 0;
            try
            {
                var list = _enemies.Alive;
                if (list.Count > _enemyX.Length) { _enemyX = new float[list.Count * 2]; _enemyY = new float[list.Count * 2]; }
                foreach (var e in list)
                {
                    var p = e.Pos;
                    _enemyX[_enemyCount] = p.x; _enemyY[_enemyCount] = p.y; _enemyCount++;
                }
            }
            catch { _enemyCount = 0; }
        }

        private struct Cast
        {
            public double T;                       // время отправителя
            public int Id;                         // BulletID
            public float Dx, Dy, Aim, P1, P2, S1, S2, S3, S4, Dr;
        }

        private readonly List<Cast> _outCasts = new List<Cast>();
        private bool _mirroring;                   // идёт наш повтор — свой хук его не пересылает
        private object _heroObj;
        private MethodInfo _bulletCreate;
        private PropertyInfo _ptrProp;
        private IntPtr _heroTrPtr;
        private bool _magicHooked;
        private int _castFails;
        private readonly Dictionary<int, int> _sentById = new Dictionary<int, int>();
        private int _castsDone, _castsDropped;

        /// <summary>Только для тестов на ПК: герой по контексту мода вместо BattleState.Hero (в тесте два «телефона» в одном процессе).</summary>
        internal static Func<ModContext, object> HeroOverride;

        private object RawHero() => HeroOverride != null ? HeroOverride(_ctx) : typeof(BattleState).GetProperty("Hero", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        private void InstallMagicHook()
        {
            try
            {
                _ctx.Before("C_Battle_AllyUnit", "Bullet_Create", 13, (self, args) => OnBulletCreate(args));
                _magicHooked = true;
            }
            catch (Exception e) { _ctx.Log("пересылка магии недоступна (нет Bullet_Create): " + e.Message); }
        }

        private void ResetMagic()
        {
            _outCasts.Clear();
            _heroObj = null; _bulletCreate = null; _heroTrPtr = IntPtr.Zero;
            _sentById.Clear(); _castsDone = 0; _castsDropped = 0; _levels.Clear();
            foreach (var p in _partners.Values) p.Casts.Clear();
        }

        private object HeroObject()
        {
            if (_heroObj != null) return _heroObj;
            try
            {
                _heroObj = RawHero();
                if (_heroObj != null)
                    _bulletCreate = _heroObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "Bullet_Create" && m.GetParameters().Length == 13);
            }
            catch { _heroObj = null; }
            return _heroObj;
        }

        /// <summary>Адрес объекта игры (у Il2Cpp-обёрток свойство Pointer); для объектов без него — null.</summary>
        private IntPtr Ptr(object o)
        {
            if (o == null) return IntPtr.Zero;
            try
            {
                if (_ptrProp == null || !_ptrProp.DeclaringType.IsInstanceOfType(o)) _ptrProp = o.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                return _ptrProp?.GetValue(o) is IntPtr ip ? ip : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>Свой герой выпустил снаряд — запомнить для отправки (только снаряды самого героя, не союзников и не наши повторы).</summary>
        private void OnBulletCreate(object[] a)
        {
            if (_mirroring || !_inRun || !_client.Connected || _partners.Count == 0 || a == null || a.Length < 13) return;
            try
            {
                var src = a[0];
                var heroTr = _heroTr;
                if (heroTr == null) { HeroPosition(out _, out _, out _); heroTr = _heroTr; }
                if (heroTr == null) return;
                bool fromHero;
                var sp = Ptr(src);
                if (sp != IntPtr.Zero) { if (_heroTrPtr == IntPtr.Zero) _heroTrPtr = Ptr(heroTr); fromHero = sp == _heroTrPtr; }
                else fromHero = ReferenceEquals(src, heroTr);
                if (!fromHero) return;
                if (_outCasts.Count >= MaxCastsPerSend) { _castsDropped++; return; }
                var hp = heroTr.position;
                var c = new Cast
                {
                    T = Clock.Elapsed.TotalSeconds,
                    Id = Convert.ToInt32(a[1]),
                    Dx = (float)(Convert.ToSingle(a[2]) - hp.x), Dy = (float)(Convert.ToSingle(a[3]) - hp.y),
                    Aim = Convert.ToSingle(a[5]), P1 = Convert.ToSingle(a[6]), P2 = Convert.ToSingle(a[7]),
                    S1 = Convert.ToSingle(a[8]), S2 = Convert.ToSingle(a[9]), S3 = Convert.ToSingle(a[10]), S4 = Convert.ToSingle(a[11]),
                    Dr = Convert.ToSingle(a[12]),
                };
                _outCasts.Add(c);
                _sentById[c.Id] = _sentById.TryGetValue(c.Id, out var n) ? n + 1 : 1;
            }
            catch (Exception e) { if (++_castFails <= 3) _ctx.Log("снаряд: " + e.Message); }
        }

        /// <summary>Отправить накопленные касты одной пачкой (вызывается вместе с отправкой состояния).</summary>
        private void FlushCasts()
        {
            if (_outCasts.Count == 0) return;
            var batch = _outCasts.ToArray();
            _outCasts.Clear();
            if (!_client.Connected) return;
            _client.SendEvent(Protocol.Build(w =>
            {
                w.WriteString("kind", "casts");
                w.WriteStartArray("c");
                foreach (var c in batch)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(Math.Round(c.T, 4)); w.WriteNumberValue(c.Id);
                    w.WriteNumberValue(Math.Round(c.Dx, 3)); w.WriteNumberValue(Math.Round(c.Dy, 3));
                    w.WriteNumberValue(Math.Round(c.Aim, 3)); w.WriteNumberValue(Math.Round(c.P1, 3)); w.WriteNumberValue(Math.Round(c.P2, 3));
                    w.WriteNumberValue(Math.Round(c.S1, 3)); w.WriteNumberValue(Math.Round(c.S2, 3)); w.WriteNumberValue(Math.Round(c.S3, 3)); w.WriteNumberValue(Math.Round(c.S4, 3));
                    w.WriteNumberValue(Math.Round(c.Dr, 3));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }));
        }

        /// <summary>Пришли касты друга — в очередь; выполняются в кадре, когда до них дойдёт показ союзника.</summary>
        private void OnRemoteCasts(Partner p, JsonElement e)
        {
            if (!e.TryGetProperty("c", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            p.Mirror = true;
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Array || it.GetArrayLength() < 12) continue;
                float F(int i) => it[i].TryGetDouble(out var d) && double.IsFinite(d) ? (float)d : 0f;
                var c = new Cast { T = it[0].TryGetDouble(out var t) ? t : 0, Id = (int)F(1), Dx = F(2), Dy = F(3), Aim = F(4), P1 = F(5), P2 = F(6), S1 = F(7), S2 = F(8), S3 = F(9), S4 = F(10), Dr = F(11) };
                if (Math.Abs(c.Dx) > 40 || Math.Abs(c.Dy) > 40) continue;          // заведомо мусор
                if (p.Casts.Count < 200) p.Casts.Enqueue(c);
            }
        }

        /// <summary>Каждый кадр: выполнить касты друга, до которых дошёл показ (та же задержка, что у движения союзника).</summary>
        private void RunCasts(Partner p, double now, double heroZ)
        {
            if (p.Casts.Count == 0) return;
            if (p.Ghost == null || !p.Ghost.Alive || p.GhostTr == null || _spells.Paused) return;
            double renderT = p.HasClock ? now - p.ClockOff - RenderDelay(p) : double.MaxValue;
            RefreshEnemyCache(now);
            int done = 0;
            while (p.Casts.Count > 0 && p.Casts.Peek().T <= renderT && done < MaxCastsPerFrame && _frameCastWatch.Elapsed.TotalMilliseconds < CastBudgetMs)
            {
                var c = p.Casts.Dequeue();
                if (p.HasClock && renderT - c.T > 1.0) { _castsDropped++; continue; }      // слишком старый (был на паузе / лаг)
                ExecuteCast(p, c, heroZ);
                done++;
            }
        }

        private void ExecuteCast(Partner p, Cast c, double z)
        {
            var hero = HeroObject();
            if (hero == null || _bulletCreate == null) return;
            try
            {
                var gp = p.GhostTr.position;
                double tx = gp.x + c.Dx, ty = gp.y + c.Dy;
                // цель друга была врагом в его мире; у нас бьём ближайшего врага рядом с той же точкой, если он есть
                // поиск по запомненным позициям врагов (обновляются 4 раза в секунду), а не перебором всех врагов на каждый снаряд
                double best = 2.5 * 2.5;
                for (int i = 0; i < _enemyCount; i++)
                {
                    double ddx = _enemyX[i] - tx, ddy = _enemyY[i] - ty, d2 = ddx * ddx + ddy * ddy;
                    if (d2 <= best) { best = d2; tx = _enemyX[i]; ty = _enemyY[i]; }
                }
                float ratio = (float)(c.Dr * Math.Max(0, _ctx.Setting("mirrorDmg")) / 100.0);
                _mirroring = true;
                _ctx.Mark("повтор снаряда друга");
                _bulletCreate.Invoke(hero, new object[] { p.GhostTr, c.Id, (float)tx, (float)ty, (float)z, c.Aim, c.P1, c.P2, c.S1, c.S2, c.S3, c.S4, ratio });
                _castsDone++;
            }
            catch (Exception e) { if (++_castFails <= 3) _ctx.Log("повтор снаряда: " + (e.InnerException ?? e).Message); }
            finally { _mirroring = false; }
        }

        /// <summary>Строка в журнал в конце боя: какие снаряды ушли друзьям — по ней видно, каких заклинаний не хватает.</summary>
        private void LogMagicSummary()
        {
            if (!_magicHooked) return;
            var lv = _levels.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}");
            _ctx.Log($"магия за бой: отправлено {string.Join(", ", _sentById.OrderBy(k => k.Key).Select(k => $"снаряд {k.Key}×{k.Value}"))}; " +
                     $"повторено касов друзей {_castsDone}, пропущено {_castsDropped}; свои заклинания (ID:уровень) {string.Join(" ", lv)}");
        }
    }
}
