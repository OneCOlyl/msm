using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MagicSurvivalCoop.Net;
using ModKit;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Магия друга: каждый снаряд героя (C_Battle_AllyUnit.Bullet_Create) пересылается друзьям и повторяется у них из точки
    /// союзника-друга, с той же задержкой, что и его движение. Урон повтора игра считает по герою получателя, а вид снаряда
    /// (размер, площадь, скорость, время, число) берёт из полей героя друга, подставленных на время вызова.
    /// </summary>
    internal sealed partial class Session
    {
        /// <summary>Заклинание союзника, который сам не стреляет, а только повторяет касты друга.</summary>
        private const int NoOwnFire = 99;
        private const int MaxCastsPerSend = 40;
        private const int MaxCastsPerFrame = 3;    // Повтор снаряда делает сама игра, его надо дозировать
        private const double CastBudgetMs = 2;     // Повтор дольше бюджета заканчивает повторы этого кадра

        private readonly Dictionary<int, (double max, double sum, int n)> _castCost = new Dictionary<int, (double, double, int)>();
        private double _frameMaxMs, _frameCastMs;
        private int _slowFrames, _slowFramesCasts;

        private float[] _enemyX = new float[64], _enemyY = new float[64];
        private int _enemyCount;
        private double _enemyCacheAt = -1;
        private readonly System.Diagnostics.Stopwatch _frameCastWatch = new System.Diagnostics.Stopwatch();

        private void BeginFrameCasts() { _frameCastWatch.Restart(); _frameCastMs = 0; }

        private void EndFrame(double frameMs)
        {
            if (frameMs > _frameMaxMs) _frameMaxMs = frameMs;
            if (frameMs > 8) { _slowFrames++; if (_frameCastMs > frameMs * 0.5) _slowFramesCasts++; }
        }

        /// <summary>Не чаще 4 раз в секунду и только когда есть что повторять.</summary>
        private void RefreshEnemyCache(double now)
        {
            if (now - _enemyCacheAt < 0.25) return;
            _enemyCacheAt = now;
            _enemyCount = 0;
            try
            {
                var list = AliveNow();
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
            public double T;                       // По часам отправителя
            public int Id;                         // BulletID
            public float Dx, Dy, Aim, P1, P2, S1, S2, S3, S4, Dr;
            public bool NoPos;                     // Игра создала снаряд в (0, 0): цель он выбирает сам
        }

        private readonly List<Cast> _outCasts = new List<Cast>();
        private const double WorldReplayRatio = 0.001;
        private bool _mirroring;                   // Свой хук не пересылает наш же повтор
        private object _heroObj;
        private MethodInfo _bulletCreate;
        private bool _magicHooked;
        private int _castFails;
        private readonly Dictionary<int, int> _sentById = new Dictionary<int, int>();
        private int _castsDone, _castsDropped;

        // Поля героя, которые читает Bullet_Create, кроме урона, крита и счётчиков самой игры
        private static readonly string[] LookFields =
        {
            "UnitApply_ArcaneRayNum", "UnitApply_ArcaneRaySize", "UnitApply_ArcaneRayTime", "UnitApply_BlizzardArea",
            "UnitApply_ElectricSize", "UnitApply_EnergyBulletMSpeed", "UnitApply_EnergyBulletSize", "UnitApply_FireballAccuracyAim",
            "UnitApply_FireballMSpeed", "UnitApply_FireballPierceNum", "UnitApply_FireballSize", "UnitApply_GhostArea",
            "UnitApply_IceNovaArea", "UnitApply_IceNovaFreezeTime", "UnitApply_IncinerateBSpeed", "UnitApply_IncinerateSize",
            "UnitApply_LavaFieldSize", "UnitApply_LavaFieldTime", "UnitApply_MagicBulletAccuracyAim", "UnitApply_MagicBulletMSpeed",
            "UnitApply_MagicBulletPierceNum", "UnitApply_MagicBulletSize", "UnitApply_MeteorArea", "UnitApply_ShieldArea",
            "UnitApply_SpiritBulletMSpeed", "UnitApply_SpiritBulletSize", "UnitApply_ThunderArea", "UnitApply_TornadoMoveSpeed",
            "UnitApply_TornadoNum", "UnitApply_TornadoSizeExpand", "UnitApply_TornadoTime", "UnitApply_TsunamiSize",
            "ApplyTornadoSize_Cal", "ApplyLevelUpExplosion_Area",
        };
        private readonly double[] _myLook = Enumerable.Repeat(double.NaN, LookFields.Length).ToArray();
        private readonly double[] _lookSaved = new double[LookFields.Length];
        private readonly bool[] _lookPut = new bool[LookFields.Length];
        private double _lookAt = -1;
        private int _lookFails;
        // Рефлексия по полю IL2CPP стоит десятки микросекунд, а повтор меняет до 34 полей на каждый снаряд: пишем прямо в память героя
        private object _lookHero;
        private IntPtr _lookPtr;
        private int[] _lookOff;
        private bool[] _lookInt;

        /// <summary>Для тестов на ПК: два «телефона» в одном процессе.</summary>
        internal static Func<ModContext, object> HeroOverride;

        private object RawHero() => HeroOverride != null ? HeroOverride(_ctx) : typeof(BattleState).GetProperty("Hero", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        private void InstallMagicHook()
        {
            try
            {
                _ctx.Before("C_Battle_AllyUnit", "Bullet_Create", 13, Hook("снаряд героя: пересылка друзьям", (self, args) => OnBulletCreate(args)));
                _magicHooked = true;
            }
            catch (Exception e) { _ctx.Log("пересылка магии недоступна (нет Bullet_Create): " + e.Message); }
        }

        private void ResetMagic()
        {
            _outCasts.Clear();
            _heroObj = null; _bulletCreate = null;
            ResetOrbits();
            _sentById.Clear(); _castsDone = 0; _castsDropped = 0; _levels.Clear();
            _lookAt = -1; Array.Fill(_myLook, double.NaN); _lookHero = null;
            _castCost.Clear(); _frameMaxMs = 0; _slowFrames = 0; _slowFramesCasts = 0;
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


        private void OnBulletCreate(object[] a)
        {
            if (_mirroring || !_inRun || !_client.Connected || _partners.Count == 0 || a == null || a.Length < 13) return;
            try
            {
                var src = a[0];
                var heroTr = _heroTr;
                if (heroTr == null) { HeroPosition(out _, out _, out _); heroTr = _heroTr; }
                if (heroTr == null) return;
                // Все снаряды героя, включая Духов и двойников игры, кроме выстрелов наших союзников-друзей
                var sp = PtrOf(src);
                foreach (var pp in _partners.Values)
                {
                    if (pp.GhostTr == null && pp.Ghost != null) EnsureGhostTr(pp);
                    if (pp.GhostTr == null) continue;
                    if (sp != IntPtr.Zero ? sp == PtrOf(pp.GhostTr) : ReferenceEquals(src, pp.GhostTr)) return;
                }
                if (_outCasts.Count >= MaxCastsPerSend) { _castsDropped++; return; }
                var hp = heroTr.position;
                float x = Convert.ToSingle(a[2]), y = Convert.ToSingle(a[3]);
                bool noPos = x == 0 && y == 0;
                var c = new Cast
                {
                    T = Clock.Elapsed.TotalSeconds,
                    Id = Convert.ToInt32(a[1]),
                    NoPos = noPos,
                    Dx = noPos ? 0 : x - hp.x, Dy = noPos ? 0 : y - hp.y,
                    Aim = Convert.ToSingle(a[5]), P1 = Convert.ToSingle(a[6]), P2 = Convert.ToSingle(a[7]),
                    S1 = Convert.ToSingle(a[8]), S2 = Convert.ToSingle(a[9]), S3 = Convert.ToSingle(a[10]), S4 = Convert.ToSingle(a[11]),
                    Dr = Convert.ToSingle(a[12]),
                };
                _outCasts.Add(c);
                _sentById[c.Id] = _sentById.TryGetValue(c.Id, out var n) ? n + 1 : 1;
            }
            catch (Exception e) { if (++_castFails <= 3) _ctx.Log("снаряд: " + e.Message); }
        }

        private void FlushCasts()
        {
            if (_outCasts.Count == 0) return;
            if (_client.Connected) _client.SendEvent(Protocol.Build(WriteCasts));
            _outCasts.Clear();
        }

        private void WriteCasts(Utf8JsonWriter w)
        {
            w.WriteString("kind", "casts");
            w.WriteStartArray("c");
            foreach (var c in _outCasts)
            {
                w.WriteStartArray();
                w.WriteNumberValue(Math.Round(c.T, 4)); w.WriteNumberValue(c.Id);
                w.WriteNumberValue(Math.Round(c.Dx, 3)); w.WriteNumberValue(Math.Round(c.Dy, 3));
                w.WriteNumberValue(Math.Round(c.Aim, 3)); w.WriteNumberValue(Math.Round(c.P1, 3)); w.WriteNumberValue(Math.Round(c.P2, 3));
                w.WriteNumberValue(Math.Round(c.S1, 3)); w.WriteNumberValue(Math.Round(c.S2, 3)); w.WriteNumberValue(Math.Round(c.S3, 3)); w.WriteNumberValue(Math.Round(c.S4, 3));
                w.WriteNumberValue(Math.Round(c.Dr, 3));
                if (c.NoPos) w.WriteNumberValue(1);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        /// <summary>Касты выполняются в кадре, когда до них дойдёт показ союзника.</summary>
        private void OnRemoteCasts(Partner p, JsonElement e)
        {
            if (!e.TryGetProperty("c", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            p.Mirror = true;
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Array || it.GetArrayLength() < 12) continue;
                float F(int i) => it[i].TryGetDouble(out var d) && double.IsFinite(d) ? (float)d : 0f;
                var c = new Cast { T = it[0].TryGetDouble(out var t) ? t : 0, Id = (int)F(1), Dx = F(2), Dy = F(3), Aim = F(4), P1 = F(5), P2 = F(6), S1 = F(7), S2 = F(8), S3 = F(9), S4 = F(10), Dr = F(11), NoPos = it.GetArrayLength() >= 13 && F(12) != 0 };
                if (Math.Abs(c.Dx) > 40 || Math.Abs(c.Dy) > 40) continue;
                if (p.Casts.Count < 200) p.Casts.Enqueue(c);
            }
        }

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
                if (p.HasClock && renderT - c.T > 1.0) { _castsDropped++; continue; }      // Слишком старый: была пауза или лаг
                double t0 = _frameCastWatch.Elapsed.TotalMilliseconds;
                ExecuteCast(p, c, heroZ);
                double cost = _frameCastWatch.Elapsed.TotalMilliseconds - t0;
                _frameCastMs += cost;
                var cc = _castCost.TryGetValue(c.Id, out var v) ? v : (0, 0, 0);
                _castCost[c.Id] = (Math.Max(cc.Item1, cost), cc.Item2 + cost, cc.Item3 + 1);
                done++;
                if (cost > CastBudgetMs) break;
            }
        }

        private void ExecuteCast(Partner p, Cast c, double z)
        {
            var hero = HeroObject();
            if (hero == null || _bulletCreate == null) return;
            try
            {
                var gp = p.GhostTr.position;
                double tx = 0, ty = 0;
                if (c.NoPos) z = 0;
                else
                {
                    tx = gp.x + c.Dx; ty = gp.y + c.Dy;
                    // Цель друга была врагом в его мире, у нас бьём ближайшего врага рядом с той же точкой
                    double best = 2.5 * 2.5;
                    for (int i = 0; i < _enemyCount; i++)
                    {
                        double ddx = _enemyX[i] - tx, ddy = _enemyY[i] - ty, d2 = ddx * ddx + ddy * ddy;
                        if (d2 <= best) { best = d2; tx = _enemyX[i]; ty = _enemyY[i]; }
                    }
                }
                // Урон друга по общим врагам идёт через здоровье копий. Не 0: при 0 не видно Маг. круга
                float ratio = (float)(c.Dr * (ReplayWithoutDamage(p) ? WorldReplayRatio : Math.Max(0, _ctx.Setting("mirrorDmg")) / 100.0));
                _mirroring = true;
                _ctx.Mark("повтор снаряда друга");
                PutLook(hero, p.Look);
                _bulletCreate.Invoke(hero, new object[] { p.GhostTr, c.Id, (float)tx, (float)ty, (float)z, c.Aim, c.P1, c.P2, c.S1, c.S2, c.S3, c.S4, ratio });
                _castsDone++;
            }
            catch (Exception e) { if (++_castFails <= 3) _ctx.Log("повтор снаряда: " + (e.InnerException ?? e).Message); }
            finally { _mirroring = false; RestoreLook(hero); }
        }

        /// <summary>Раз в секунду, это чтение полей героя через рефлексию. Между чтениями «fx» в состояние не пишется.</summary>
        private void WriteMyLook(Utf8JsonWriter w)
        {
            double now = Clock.Elapsed.TotalSeconds;
            if (now - _lookAt < 1) return;
            _lookAt = now;
            var hero = HeroObject();
            if (hero == null) return;
            try
            {
                for (int i = 0; i < LookFields.Length; i++) _myLook[i] = GetLook(hero, i);
            }
            catch (Exception e) { if (++_lookFails <= 3) _ctx.Log("вид магии: " + Inner(e).Message); return; }
            w.WriteStartArray("fx");
            foreach (var v in _myLook) w.WriteNumberValue(double.IsFinite(v) ? Math.Round(v, 4) : -1);
            w.WriteEndArray();
        }

        private static void ReadLook(Partner p, JsonElement s)
        {
            if (!s.TryGetProperty("fx", out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != LookFields.Length) return;
            var look = p.Look ??= new double[LookFields.Length];
            int i = 0;
            foreach (var v in a.EnumerateArray()) look[i++] = v.TryGetDouble(out var d) && d >= 0 ? d : double.NaN;
        }

        /// <summary>Только поля, где друг отличается от нас: свои значения читаются заново, они могли смениться за секунду.</summary>
        private void PutLook(object hero, double[] look)
        {
            if (look == null) return;
            PrepareLook(hero);
            for (int i = 0; i < look.Length; i++)
            {
                if (double.IsNaN(look[i]) || look[i] == _myLook[i]) continue;
                try
                {
                    double own = GetLook(hero, i);
                    if (double.IsNaN(own) || own == look[i]) continue;
                    SetLook(hero, i, look[i]);
                    _lookSaved[i] = own; _lookPut[i] = true;
                }
                catch (Exception e) { if (++_lookFails <= 3) _ctx.Log("вид магии друга: " + Inner(e).Message); }
            }
        }

        private void RestoreLook(object hero)
        {
            for (int i = 0; i < _lookPut.Length; i++)
            {
                if (!_lookPut[i]) continue;
                _lookPut[i] = false;
                try { SetLook(hero, i, _lookSaved[i]); }
                catch (Exception e) { if (++_lookFails <= 3) _ctx.Log("вид магии: возврат: " + Inner(e).Message); }
            }
        }

        /// <summary>Смещения полей один раз на героя. Без IL2CPP (тесты на ПК) остаётся рефлексия.</summary>
        private void PrepareLook(object hero)
        {
            if (ReferenceEquals(hero, _lookHero)) return;
            _lookHero = hero; _lookOff = null;
            _lookPtr = PtrOf(hero);
            if (_lookPtr == IntPtr.Zero) return;
            try
            {
                var il2 = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType("Il2CppInterop.Runtime.IL2CPP"); } catch { return null; } })
                          .FirstOrDefault(t => t != null);
                var getClass = il2?.GetMethod("il2cpp_object_get_class");
                var getField = il2?.GetMethod("il2cpp_class_get_field_from_name");
                var getOffset = il2?.GetMethod("il2cpp_field_get_offset");
                if (getClass == null || getField == null || getOffset == null) return;
                var klass = (IntPtr)getClass.Invoke(null, new object[] { _lookPtr });
                var off = new int[LookFields.Length];
                var isInt = new bool[LookFields.Length];
                int found = 0;
                for (int i = 0; i < LookFields.Length; i++)
                {
                    var f = (IntPtr)getField.Invoke(null, new object[] { klass, LookFields[i] });
                    off[i] = f == IntPtr.Zero ? -1 : Convert.ToInt32(getOffset.Invoke(null, new object[] { f }));
                    isInt[i] = hero.GetType().GetProperty(LookFields[i], AnyInst)?.PropertyType == typeof(int);
                    if (off[i] > 0) found++;
                }
                _lookOff = off; _lookInt = isInt;
                _ctx.Log($"вид магии: полей героя по смещению {found} из {LookFields.Length}");
            }
            catch (Exception e) { _lookOff = null; if (++_lookFails <= 3) _ctx.Log("вид магии: смещения: " + Inner(e).Message); }
        }

        private double GetLook(object hero, int i)
        {
            PrepareLook(hero);
            if (_lookOff != null && _lookOff[i] > 0)
            {
                int raw = System.Runtime.InteropServices.Marshal.ReadInt32(_lookPtr + _lookOff[i]);
                return _lookInt[i] ? raw : BitConverter.Int32BitsToSingle(raw);
            }
            return GetMember(hero, LookFields[i]) is object v ? Convert.ToDouble(v) : double.NaN;
        }

        private void SetLook(object hero, int i, double v)
        {
            if (_lookOff != null && _lookOff[i] > 0)
                System.Runtime.InteropServices.Marshal.WriteInt32(_lookPtr + _lookOff[i], _lookInt[i] ? (int)Math.Round(v) : BitConverter.SingleToInt32Bits((float)v));
            else SetMember(hero, LookFields[i], v);
        }

        private void LogMagicSummary()
        {
            if (!_magicHooked) return;
            var lv = _levels.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}");
            _ctx.Log($"магия за бой: отправлено {string.Join(", ", _sentById.OrderBy(k => k.Key).Select(k => $"снаряд {k.Key}×{k.Value}"))}; " +
                     $"повторено касов друзей {_castsDone}, пропущено {_castsDropped}; свои заклинания (ID:уровень) {string.Join(" ", lv)}; " +
                     $"цена повтора (мс, макс/среднее): {string.Join(", ", _castCost.OrderByDescending(k => k.Value.max).Select(k => $"снаряд {k.Key} {k.Value.max:0.0}/{k.Value.sum / Math.Max(1, k.Value.n):0.0}"))}; " +
                     $"кадр мода: макс {_frameMaxMs:0.0} мс, дольше 8 мс — {_slowFrames} (из них из-за повторов {_slowFramesCasts})" + ArmageddonSummary());
        }
    }
}
