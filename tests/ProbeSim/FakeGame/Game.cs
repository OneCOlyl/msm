// Фальшивые классы игры (то же имя сборки и пространство имён Il2Cpp), чтобы проверить разведчика на ПК.
using System; using System.Collections.Generic; using UnityEngine;
namespace Il2Cpp
{
    public class FakeTransform { public Vector3 position { get; set; } }
    public class C_Battle_Manager
    {
        public static int StopCalls, RestartCalls;
        public bool Spawning = true;
        public List<int> Dictionary_AreaEnemyID { get; set; } = new List<int> { 1, 2, 3 };
        public C_Battle_EnemyUnit[] Array_EnemyPool { get; set; } = new C_Battle_EnemyUnit[300];
        public int EnemyCreateNum { get; set; } = 5;
        public void Update() { }
        public void Create_UnitEnemy() { }
        public void Create_UnitEnemy_Stop() { StopCalls++; Spawning = false; }
        public void Create_UnitEnemyBoss(int unitId, int count) { }
        public object StartCoroutine(string name) { if (name == "Create_UnitEnemy") { RestartCalls++; Spawning = true; } return null; }
    }
    public class C_Battle_EnemyUnit
    {
        private static long _next = 0x1000;
        public IntPtr Pointer { get; } = new IntPtr(_next += 0x40);
        public string name { get; set; } = "Enemy(Clone)";
        public FakeTransform Transform_ObjUnit { get; set; } = new FakeTransform();
        public float Unit_Hp { get; set; } = 10; public float UnitApply_MoveSpeed { get; set; } = 1.5f; public bool IsFreeze { get; set; }
        public void Start_Unit(int unitId, float varA, float varB) { }
        public void Unit_Death() { }
        public void Status_Freeze(float s) { }
    }
    public class C_Battle_EnemyUnitPool { }
}
