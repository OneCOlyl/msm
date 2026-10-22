// Фальшивые классы игры (то же имя сборки и пространство имён Il2Cpp) — чтобы проверить разведчика на ПК.
// Поведение упрощено, но повторяет найденное разведкой 1: враги двигаются корутинами, это физические тела, спавн волнами.
using System; using System.Collections.Generic; using UnityEngine; using Object = UnityEngine.Object;
namespace Il2Cpp
{
    public static class FakeWorld
    {
        public static Action<C_Battle_EnemyUnit, int, float, float> OnStartUnit;   // как хук Harmony: срабатывает на любой вызов Start_Unit
        public static readonly List<C_Battle_EnemyUnit> All = new List<C_Battle_EnemyUnit>();
        public static readonly List<C_Battle_EnemyUnit> Gone = new List<C_Battle_EnemyUnit>();
        public static double Now; public static Vector3 Hero;
        public static int KillCount;
    }
    public class FakeEnemyGO : GameObject, ICloneTarget
    {
        public Object CloneForTest()
        {
            var go = new FakeEnemyGO { name = "AObj_UnitEnemy(Clone)" };
            var e = new C_Battle_EnemyUnit { Go = go };
            go.TestComponent = e;
            return go;
        }
    }
    public class C_Battle_Manager : MonoBehaviour
    {
        public static int StopCalls;
        public GameObject AObj_UnitEnemy { get; set; } = new FakeEnemyGO { name = "AObj_UnitEnemy" };
        public int EnemyCreateNum { get; set; } = 2; public int EnemyCreateNum_Min { get; set; } = 2; public int EnemyCreateNum_Max { get; set; } = 2;
        public int AllEnemyNum { get; set; }
        public static int ApplyBossNum { get; set; }
        public void Update() { }
        public void Create_UnitEnemy_Stop() { StopCalls++; }
        public void Create_UnitEnemyBoss(int id, int num) { ApplyBossNum++; for (int i = 0; i < num; i++) C_Battle_EnemyUnit.Spawn(id, 6, 0); }
    }
    public class C_Battle_EnemyUnit : MonoBehaviour
    {
        public GameObject Go;
        public Transform Transform_ObjUnit { get; set; } = new Transform();
        public Rigidbody Rigidbody_ObjUnit { get; set; } = new Rigidbody();
        public SpriteRenderer Motion_Unit { get; set; } = new SpriteRenderer();
        public Transform Transform_ObjUnitSprite { get; set; } = new Transform();
        public bool Unit_IsLive { get; set; } public float Unit_Hp { get; set; } public float UnitApply_HpMax { get; set; } public int UnitID { get; set; }
        public float UnitApply_UnitSize { get; set; } = 1.25f;
        public static C_Battle_EnemyUnit Spawn(int id, double dx, double dy)
        {
            var e = new C_Battle_EnemyUnit { name = "1" + FakeWorld.All.Count.ToString("0000") };
            e.Transform_ObjUnit.position = new Vector3((float)(FakeWorld.Hero.x + dx), (float)(FakeWorld.Hero.y + dy), 0);
            e.Start_Unit(id, 0, 0);
            return e;
        }
        public void Start_Unit(int unitId, float a, float b)
        {
            UnitID = unitId; Unit_IsLive = true; Unit_Hp = UnitApply_HpMax = 20; CoroutinesStopped = false;
            if (!FakeWorld.All.Contains(this)) FakeWorld.All.Add(this);
            FakeWorld.OnStartUnit?.Invoke(this, unitId, a, b);
        }
        public void Unit_Destroy_Simple() { Unit_IsLive = false; FakeWorld.All.Remove(this); FakeWorld.Gone.Add(this); }
        public void Unit_Death() { Unit_IsLive = false; FakeWorld.All.Remove(this); FakeWorld.Gone.Add(this); FakeWorld.KillCount++; }
    }
}
