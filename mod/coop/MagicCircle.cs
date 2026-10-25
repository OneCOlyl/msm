using System;
using System.Collections.Generic;
using System.Text.Json;
using MagicSurvivalCoop.Net;
using UnityEngine;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Маг. круг (заклинание 19) друга у его союзника (1.5.9). Снарядов у круга нет — повтор магии его не видит.
    /// На герое (разведка 0.5.1): флаг IsMagicCircle_P, объект Obj_MagicCircleMid («Unit_MagicCircleMid», три круга-ребёнка),
    /// круги Transform_ObjMagicCircleA/B/C с рендерами SpriteRenderer_MagicCircleA/B/C (кадры MagicCircle1…5).
    /// Друг шлёт: включён ли Mid, масштаб, кадры и поворот трёх кругов; у получателя — копия его Obj_MagicCircleMid на союзнике.
    /// Урон круга друга уже идёт хосту через здоровье копий врагов (общие враги) — копия только для вида.
    /// </summary>
    internal sealed partial class Session
    {
        private static readonly string[] CircleKeys = { "A", "B", "C" };

        private bool _myMc, _myMcOn;
        private object _myMcObj;
        private readonly string[] _myMcImg = new string[3];
        private readonly double[] _myMcRot = new double[3];
        private double _myMcScale = 1;
        private bool _mcLogged;

        /// <summary>Отправитель, при каждой отправке: есть ли круг, включён ли, кадры, повороты, масштаб.</summary>
        private static readonly string[] McRendererNames = { "SpriteRenderer_MagicCircleA", "SpriteRenderer_MagicCircleB", "SpriteRenderer_MagicCircleC" };
        private static readonly string[] McTransformNames = { "Transform_ObjMagicCircleA", "Transform_ObjMagicCircleB", "Transform_ObjMagicCircleC" };
        private double _mcLookAt;

        private void RefreshMyCircle()
        {
            var hero = HeroObject();
            _myMc = hero != null && GetMember(hero, "IsMagicCircle_P") is bool b && b;
            _myMcObj = _myMc ? GetMember(hero, "Obj_MagicCircleMid") : null;
            _myMcOn = _myMcObj != null && GetMember(_myMcObj, "activeSelf") is bool on && on;
            if (!_myMcOn) return;
            try
            {
                // поворот — каждую отправку (вращение у друга плавное); масштаб и кадры меняются редко — не чаще 10 раз в секунду:
                // это десяток обращений к игре через рефлексию (1.6.19, телефоны: свои орбиты — 1,2–1,4 мс на отправку при 30 в секунду)
                double now = Clock.Elapsed.TotalSeconds;
                bool look = now >= _mcLookAt;
                if (look)
                {
                    _mcLookAt = now + 0.1;
                    if (GetMember(GetMember(_myMcObj, "transform"), "localScale") is Vector3 ls) _myMcScale = ls.x;
                }
                for (int i = 0; i < 3; i++)
                {
                    if (look) _myMcImg[i] = NameOf(SpriteOf(GetMember(hero, McRendererNames[i])));
                    _myMcRot[i] = GetMember(GetMember(hero, McTransformNames[i]), "localEulerAngles") is Vector3 e ? e.z : 0;
                }
                if (!_mcLogged) { _mcLogged = true; _ctx.Log($"маг. круг (свой): {TreeOf(_myMcObj)}; кадры {string.Join(", ", _myMcImg)}"); }
            }
            catch (Exception e) { if (++_orbFails <= 6) _ctx.Log("маг. круг: " + Inner(e).Message); }
        }

        private void WriteMyCircle(Utf8JsonWriter w)
        {
            if (!_myMc) return;
            w.WriteNumber("mc", 1);
            w.WriteNumber("mca", _myMcOn ? 1 : 0);
            if (!_myMcOn) return;
            w.WriteNumber("mcs", Math.Round(_myMcScale, 3));
            w.WriteStartArray("mci"); foreach (var s in _myMcImg) w.WriteStringValue(s ?? ""); w.WriteEndArray();
            w.WriteStartArray("mcr"); foreach (var r in _myMcRot) w.WriteNumberValue(Math.Round(r, 1)); w.WriteEndArray();
        }

        private static void ReadCircle(OrbState st, JsonElement o)
        {
            st.Mc = Protocol.Bool(o, "mc");
            st.McOn = st.Mc && Protocol.Bool(o, "mca");
            if (!st.McOn) return;
            st.McS = Protocol.Num(o, "mcs", 1);
            if (o.TryGetProperty("mci", out var im) && im.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < 3 && i < im.GetArrayLength(); i++) st.McImg[i] = im[i].GetString();
            if (o.TryGetProperty("mcr", out var rr) && rr.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < 3 && i < rr.GetArrayLength(); i++) st.McRot[i] = rr[i].TryGetDouble(out var d) ? d : 0;
        }

        /// <summary>Получатель, каждый кадр: копия круга (создать, включить, кадры, повороты).</summary>
        private void UpdateCircleCopy(Partner p, OrbVisuals v, OrbState st, object hero, double now)
        {
            if (st.Mc && v.Mc == null && !v.McFailed)
            {
                v.Mc = MakeClone(GetMember(hero, "Obj_MagicCircleMid"));
                if (v.Mc == null) { v.McFailed = true; _ctx.Log("маг. круг: шаблона Obj_MagicCircleMid нет"); return; }
                var getChild = v.Mc.Tr.GetType().GetMethod("GetChild", new[] { typeof(int) });
                int n = Convert.ToInt32(GetMember(v.Mc.Tr, "childCount") ?? 0);
                for (int i = 0; i < n && getChild != null; i++)
                {
                    var ctr = getChild.Invoke(v.Mc.Tr, new object[] { i }) as Transform;
                    var cgo = GetMember(ctr, "gameObject");
                    if (ctr != null && cgo != null) v.McKids.Add(new OrbClone { Go = cgo, Tr = ctr, Sr = GetComponentOf(cgo) });
                }
            }
            if (v.Mc == null) return;
            Show(v.Mc, st.McOn);
            if (!st.McOn) return;
            for (int i = 0; i < v.McKids.Count && i < 3; i++)
            {
                var k = v.McKids[i];
                if (!string.IsNullOrEmpty(st.McImg[i])) ApplyImage(k, st.McImg[i], now, "маг. круг");
                try { k.Tr.GetType().GetProperty("localEulerAngles")?.SetValue(k.Tr, new Vector3(0, 0, (float)st.McRot[i])); } catch { }
            }
            if (!v.McLogged) { v.McLogged = true; _ctx.Log($"маг. круг «{p.Name}»: копия {TreeOf(v.Mc.Go)}; кадры от друга {string.Join(", ", st.McImg)}"); }
        }

        private static void PlaceCircleCopy(OrbVisuals v, OrbState st, Vector3 g)
        {
            if (v.Mc == null || !st.McOn) return;
            v.Mc.Tr.position = new Vector3(g.x, g.y, g.z);
            if (st.McS != v.McS)
            {
                v.McS = st.McS;
                try { v.Mc.Tr.GetType().GetProperty("localScale")?.SetValue(v.Mc.Tr, new Vector3((float)st.McS, (float)st.McS, 1f)); } catch { }
            }
        }
    }
}
