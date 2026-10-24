// Заглушки ТОЛЬКО для компиляции и тестов без телефона. На устройстве используются настоящие сборки Il2CppInterop
// (имена сборок и типов совпадают, версия 0.0.0.0 — не выше настоящей, поэтому привязка проходит).
// Моды обращаются к Unity только через членов, которые использует и само ядро ModKit; остальное здесь — для тестов (через рефлексию).
using System;
namespace UnityEngine
{
    public struct Vector2 { public float x; public float y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
    public struct Vector3 { public float x; public float y; public float z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    public struct Color { public float r, g, b, a; public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; } }
    public class Object
    {
        public string name { get; set; }
        public static Object Instantiate(Object original) => original is ICloneTarget c ? c.CloneForTest() : null;
        public bool DestroyedForTest;
        public static void Destroy(Object o) { if (o != null) o.DestroyedForTest = true; }
    }
    /// <summary>Только для тестов: как копировать фальшивые объекты.</summary>
    public interface ICloneTarget { Object CloneForTest(); }
    public class Component : Object { public GameObject gameObject { get; set; } public Transform transform => gameObject?.transform; }
    public class Transform : Component { public Vector3 position { get; set; } public Vector3 localScale { get; set; } public int childCount { get; set; } public Transform parent { get; set; } public new Transform transform => this; }
    public class Renderer : Component { }
    public class SpriteRenderer : Renderer { public Color color { get; set; } public bool enabled { get; set; } = true; public Sprite sprite { get; set; } }
    public class Sprite : Object { }
    public static class Resources
    {
        public static System.Collections.Generic.List<Object> TestAll = new System.Collections.Generic.List<Object>();
        public static int TestScans;
        public static T[] FindObjectsOfTypeAll<T>() where T : Object { TestScans++; return System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OfType<T>(TestAll)); }
    }
    public class RectTransform : Transform { }
    public class GameObject : Object
    {
        public bool activeSelf { get; set; }
        public Transform transform { get; set; } = new Transform();
        public Component TestComponent;
        public void SetActive(bool v) => activeSelf = v;
        public T GetComponent<T>() where T : class => TestComponent as T;
        public T GetComponentInChildren<T>(bool includeInactive) where T : class => TestComponent as T;
    }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { public bool CoroutinesStopped; public void StopAllCoroutines() => CoroutinesStopped = true; }
    public class Rigidbody : Component { public bool isKinematic { get; set; } }
    public static class Time { public static float timeScale { get; set; } = 1; }
}
