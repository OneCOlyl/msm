using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop
{
    // Режим ставят сами кнопки старта: Button_AreaStart 0, Button_AreaStart_Loop 1.
    internal sealed partial class Session
    {
        private static readonly string[] StartButtons = { "Button_AreaStart", "Button_AreaStart_Loop", "Button_AreaSpecialSheet_Start" };
        private const int LevelArea = 14;
        private static readonly Dictionary<string, PropertyInfo> DbProps = new Dictionary<string, PropertyInfo>();
        private static Type _dbType;

        private object _menu;                      // A_MainMenuInformation текущей сцены меню
        private double _menuLookAt, _lobbyAt, _levelFixAt;
        private bool _onAreaScreen, _lobbyWaited;
        private int _startButton = -1;
        private int _shownArea = -1;
        private readonly List<GameObject> _hiddenArrows = new List<GameObject>(), _hiddenStarts = new List<GameObject>();
        private UiKit.HudButton _lobbyBtn, _chestBtn;
        private Action _lobbyAction;

        private void InstallLobby()
        {
            _lobbyAction = LobbyStep;
            for (int i = 0; i < StartButtons.Length; i++)
                try
                {
                    string m = StartButtons[i];
                    int button = i;
                    // До кнопки: проверка платной локации идёт по Game_AreaID. После: кнопка сама ставит Game_BattleLoopON
                    _ctx.Before("A_MainMenuInformation", m, 0, Hook("лобби: " + m, (self, a) => { if (_hosting) _startButton = button; ForceHostArea(false); }));
                    _ctx.After("A_MainMenuInformation", m, 0, Hook("лобби: " + m, (self, a) => ForceHostArea(true)));
                }
                catch (Exception e) { _ctx.Log($"лобби: хук {StartButtons[i]} недоступен: {e.Message}"); }
        }

        private Partner LobbyHost() => _hosting ? null
            : _partners.Values.FirstOrDefault(p => p.LobbyArea >= 0 && p.HasState && _clockMs - p.StateAt < 3000);

        private (int area, int level, int loop, int button, bool chests, bool ready) LobbyNow()
        {
            if (!_hosting) return (-1, 0, -1, -1, false, !_inRun && _onAreaScreen && LobbyHost() != null);
            int area = DbInt("Game_AreaID");
            if (area < 0) return (-1, 0, -1, -1, false, false);
            return (area, DbInt("Game_AreaLevel"), DbInt("Game_BattleLoopON"), _startButton, ChestsOn, false);
        }

        // Лобби уходит не в каждом состоянии, поэтому «la» есть всегда, когда пишет хост, а «rdy», когда гость.
        private void WriteLobby(System.Text.Json.Utf8JsonWriter w, (int area, int level, int loop, int button, bool chests, bool ready) l)
        {
            if (!_hosting) { w.WriteNumber("rdy", l.ready ? 1 : 0); return; }
            w.WriteNumber("la", l.area);
            if (l.area < 0) return;
            w.WriteNumber("ll", l.level);
            w.WriteNumber("lp", l.loop);
            if (l.button >= 0) w.WriteNumber("lb", l.button);
            if (l.chests) w.WriteNumber("chs", 1);
        }

        private void ReadLobby(Partner p, System.Text.Json.JsonElement s)
        {
            if (s.TryGetProperty("la", out _))
            {
                p.LobbyArea = (int)Net.Protocol.Num(s, "la", -1);
                p.LobbyLevel = (int)Net.Protocol.Num(s, "ll", 0);
                p.LobbyLoop = (int)Net.Protocol.Num(s, "lp", -1);
                p.LobbyButton = (int)Net.Protocol.Num(s, "lb", -1);
                p.LobbyChests = Net.Protocol.Bool(s, "chs");
            }
            if (!s.TryGetProperty("rdy", out _)) return;
            bool was = p.Ready;
            p.Ready = Net.Protocol.Bool(s, "rdy");
            if (_hosting && p.Ready && !was) _ctx.Toast($"{p.Name} готов к бою", 2);
        }

        private void LobbyStep()
        {
            double now = Clock.Elapsed.TotalSeconds;
            if (now < _lobbyAt) return;
            _lobbyAt = now + 0.2;
            bool together = _client.Connected && _partners.Count > 0;
            _onAreaScreen = together && AreaScreenOpen(now);
            var host = together ? LobbyHost() : null;
            if (host != null) ApplyHostArea(host, now);

            if (host == null || !_onAreaScreen)
            {
                ShowAll(_hiddenArrows); ShowAll(_hiddenStarts);
                _lobbyWaited = false;
                UpdateLobbyLabel(_hosting && _onAreaScreen ? HostLobbyText() : null);
                UpdateChestButton(_hosting && _onAreaScreen ? ChestsOn : (bool?)null);
                return;
            }

            UpdateChestButton(host.LobbyChests);
            Hide("Obj_ButtonAreaMoveL", _hiddenArrows);
            Hide("Obj_ButtonAreaMoveR", _hiddenArrows);
            if (!host.Battle)
            {
                Hide("Obj_ButtonAreaStart", _hiddenStarts);
                Hide("Obj_ButtonAreaStart_Loop", _hiddenStarts);
                _lobbyWaited = true;
                UpdateLobbyLabel($"Локацию выбирает {host.Name}, ждём старта");
                return;
            }
            ShowAll(_hiddenStarts);
            UpdateLobbyLabel($"{host.Name} в бою — входите");
            if (!_lobbyWaited) return;
            _lobbyWaited = false;
            string button = host.LobbyButton >= 0 && host.LobbyButton < StartButtons.Length ? StartButtons[host.LobbyButton]
                : host.LobbyLoop == 1 ? "Button_AreaStart_Loop" : "Button_AreaStart";
            _ctx.Log($"лобби: хост начал бой — вхожу через {button} (локация {host.LobbyArea}, сложность {host.LobbyLevel}, режим {host.LobbyLoop})");
            CallMethod(_menu, button);
        }

        private string HostLobbyText()
        {
            int ready = _partners.Values.Count(p => p.Ready);
            return $"Готовы к бою: {ready} из {_partners.Count}";
        }

        // Сложность могла не открыться у гостя, поэтому повтор не чаще раза в секунду.
        private void ApplyHostArea(Partner host, double now)
        {
            int area = host.LobbyArea, level = Math.Max(0, host.LobbyLevel);
            if (!_onAreaScreen) _shownArea = -1;
            if (DbInt("Game_AreaID") != area) SetDbInt("Game_AreaID", area);
            // Вне экрана меняется только поле, поэтому при открытии экрана перерисовываем
            if (_onAreaScreen && _shownArea != area)
            {
                SetDbInt("Game_AreaLevel", level);
                CallMethod(_menu, "Area_Information", area);
                _shownArea = area;
            }
            if (DbInt("Game_AreaLevel") == level || now < _levelFixAt) return;
            _levelFixAt = now + 1;
            // Как Button_AreaLevel0/1: сложность выбирается только у этой локации, AreaLevelInf рисует её
            if (_onAreaScreen && area == LevelArea) CallMethod(_menu, "AreaLevelInf", LevelArea, level, 0);
            else SetDbInt("Game_AreaLevel", level);
        }

        private void ForceHostArea(bool afterButton)
        {
            var host = LobbyHost();
            if (host == null) return;
            SetDbInt("Game_AreaID", host.LobbyArea);
            SetDbInt("Game_AreaLevel", Math.Max(0, host.LobbyLevel));
            if (afterButton && host.LobbyLoop >= 0) SetDbInt("Game_BattleLoopON", host.LobbyLoop);
        }

        private bool AreaScreenOpen(double now)
        {
            if (_menu == null)
            {
                if (now < _menuLookAt) return false;
                _menuLookAt = now + 1;
                _menu = FindAllOfType("A_MainMenuInformation").FirstOrDefault();
                if (_menu == null) return false;
            }
            try { return GetAny(_menu, "Obj_FieldArea") is GameObject go && go.activeInHierarchy; }
            catch { _menu = null; return false; }
        }

        private void Hide(string field, List<GameObject> hidden)
        {
            if (!(GetAny(_menu, field) is GameObject go) || !go.activeSelf) return;
            go.SetActive(false);
            if (!hidden.Contains(go)) hidden.Add(go);
        }

        private static void ShowAll(List<GameObject> hidden)
        {
            foreach (var go in hidden) try { go.SetActive(true); } catch { }
            hidden.Clear();
        }

        private void UpdateLobbyLabel(string text)
        {
            if (text == null) { if (_lobbyBtn != null) _lobbyBtn.SetVisible(false); return; }
            if (_lobbyBtn == null || !_lobbyBtn.Alive)
                _lobbyBtn = UiKit.AddHudButton("coop_lobby", text, 20, 100, 450, 38, 16, BtnGray, () => _ctx.Guard("окно", OpenWindow));
            _lobbyBtn.SetText(text);
            _lobbyBtn.SetVisible(true);
        }

        private void UpdateChestButton(bool? on)
        {
            if (on == null) { if (_chestBtn != null) _chestBtn.SetVisible(false); return; }
            if (_chestBtn == null || !_chestBtn.Alive)
                _chestBtn = UiKit.AddHudButton("coop_chests", "", 480, 100, 220, 38, 16, BtnGray, () => _ctx.Guard("сундуки в лобби", ToggleChests));
            _chestBtn.SetText(on.Value ? "Сундуки: общие" : "Сундуки: у каждого свои");
            _chestBtn.SetVisible(true);
        }

        private void ToggleChests()
        {
            if (!_hosting) { _ctx.Toast("Сундуки выбирает хост", 2); return; }
            _ctx.SetSetting("chests", ChestsOn ? 0 : 1);
            _ctx.Log("лобби: сундуки " + (ChestsOn ? "общие" : "у каждого свои"));
            UpdateChestButton(ChestsOn);
        }

        private void ResetLobby()
        {
            try { ShowAll(_hiddenArrows); ShowAll(_hiddenStarts); } catch { }
            _menu = null;
            _onAreaScreen = _lobbyWaited = false;
            _shownArea = -1;
            if (!_hosting) _startButton = -1;
            DestroyBtn(ref _lobbyBtn);
            DestroyBtn(ref _chestBtn);
        }

        private static PropertyInfo DbProp(string name)
        {
            if (DbProps.TryGetValue(name, out var p)) return p;
            try { _dbType ??= Hooks.FindType("Z_Database"); } catch { }
            p = _dbType?.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            DbProps[name] = p;
            return p;
        }

        private static int DbInt(string name) => DbProp(name) is PropertyInfo p ? Convert.ToInt32(p.GetValue(null)) : -1;

        private static void SetDbInt(string name, int v) => DbProp(name)?.SetValue(null, v);
    }
}
