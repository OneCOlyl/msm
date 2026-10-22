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
namespace Il2Cpp
{
    using System.Collections.Generic;
    using UnityEngine;
    /// <summary>Фальшивый герой: Щит (объект с детьми), Спутники (массив объектов), Духи (список объектов).</summary>
    public class C_Battle_AllyUnit : MonoBehaviour
    {
        public Transform Transform_ObjUnit { get; set; } = new Transform { name = "1000" };
        public GameObject Obj_UnitShield { get; set; }
        public GameObject Obj_Shield { get; set; }
        public GameObject[] Array_Obj_UnitSatellitesA { get; set; }
        public List<GameObject> List_ObjSpirit { get; set; } = new List<GameObject>();
        public float UnitApply_ShieldMaxNum { get; set; } = 3;
        public bool IsShield_P { get; set; } = true;
        public int UnitApply_SpiritNum { get; set; } = 2;
        public C_Battle_AllyUnit()
        {
            Obj_UnitShield = Make("Obj_UnitShield", 0, 0, true, 3);
            Obj_Shield = Make("1100", 0, 0.03f, false, 1);
            List_ObjSpirit.Add(null);
            Array_Obj_UnitSatellitesA = new[] { Make("SatA01", 2, 0, true, 0), Make("SatA02", -2, 0, true, 0), Make("SatA03", 0, 2, false, 0) };
            List_ObjSpirit.Add(Make("Spirit01", 1, 1, true, 1));
        }
        public static readonly List<GameObject> Clones = new List<GameObject>();
        public class FakeGO : GameObject, ICloneTarget
        {
            public Object CloneForTest()
            {
                var c = Make(name + "(Clone)", transform.position.x, transform.position.y, activeSelf, transform.childCount);
                Clones.Add(c);
                return c;
            }
        }
        static GameObject Make(string n, float x, float y, bool on, int kids)
        {
            var go = new FakeGO { name = n, activeSelf = on };
            go.transform = new Transform { name = n, position = new Vector3(x, y, 0), childCount = kids, gameObject = go };
            return go;
        }
        public void Play_Shield() { }
        public void Start_Satellite() { }
        public void Spirit_Attack() { }
        public void Bullet_Create(Transform t, int id, float x, float y, float z, float a, float p1, float p2, float s1, float s2, float s3, float s4, float dr) { }
    }
}
