using System.Collections.Generic;
namespace Il2Cpp
{
    public static class Z_Database
    {
        public static List<int> List_AllyAbilityLearn { get; set; } = new List<int> { 90001 };
        public static float AllyUnitGlobal_ManaCost { get; set; } = 75;
        public static float AllyUnitGlobal_ManaStock { get; set; }
        public static List<int> List_AllyMagicLevel { get; set; } = new List<int>(new int[40]);
        public static List<int> List_ArtifactObtain { get; set; } = new List<int>();
        public static List<int> List_ApplySynergyON { get; set; } = new List<int>();
        public static List<int> List_ArtifactPossibleGrade_C { get; set; } = new List<int> { 41 };
        public static List<int> List_ArtifactPossibleGrade_B { get; set; } = new List<int> { 42 };
        public static List<int> List_ArtifactPossibleGrade_A { get; set; } = new List<int>();
        public static List<int> List_ArtifactPossibleGrade_P { get; set; } = new List<int>();
        public static List<int> List_ArtifactPossibleGrade_S { get; set; } = new List<int> { 43 };
        public static bool RecalcFromLearn = true;
        // Как игра (по разбору MS_KIT): уровни собираются заново из списка выученного + стартовое заклинание класса.
        public static void Recalc()
        {
            if (!RecalcFromLearn) return;
            for (int i = 0; i < List_AllyMagicLevel.Count; i++) List_AllyMagicLevel[i] = 0;
            List_AllyMagicLevel[1] += 1;                                     // стартовая пуля класса
            foreach (var id in List_AllyAbilityLearn) if (id >= 1 && id <= 21) List_AllyMagicLevel[id]++;
        }
    }
}
namespace Il2Cpp
{
    using System;
    using System.Collections.Generic;
    public class V_Ability_Manager
    {
        public static Action BeforeStartInformationB;                 // как хук Harmony на StartInformation_B
        public static readonly HashSet<int> Started = new HashSet<int>();   // заклинания, чьи корутины запущены
        public void Update() { }
        // уровень героя: мана копится, на пороге открывается окно выбора (как в игре: Ability_SelectON)
        public static bool Ability_SelectON { get; set; }
        public static int ManaCalls;
        public void ManaGain(float amount)
        {
            ManaCalls++; Z_Database.AllyUnitGlobal_ManaStock += amount;
            if (Z_Database.AllyUnitGlobal_ManaStock >= Z_Database.AllyUnitGlobal_ManaCost) { Z_Database.AllyUnitGlobal_ManaStock -= Z_Database.AllyUnitGlobal_ManaCost; Ability_SelectON = true; }
        }
        public void AbilityStatApply(int type)
        {
            Z_Database.Recalc();
            BeforeStartInformationB?.Invoke();
            for (int id = 1; id <= 21; id++) if (Z_Database.List_AllyMagicLevel[id] >= 1) Started.Add(id);   // StartInformation_B
        }
    }
}
