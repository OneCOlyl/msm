// Заглушки ТОЛЬКО для компиляции без телефона. На устройстве используются настоящие сборки Il2CppInterop
// (имена сборок и типов совпадают, версия 0.0.0.0 — ниже любой настоящей, поэтому привязка проходит).
namespace UnityEngine
{
    public struct Vector2 { public float x; public float y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
    public struct Vector3 { public float x; public float y; public float z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    public struct Color { public float r, g, b, a; public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; } }
    public class Object { }
    public class Component : Object { }
    public class Transform : Component { }
    public class RectTransform : Transform { }
    public class GameObject : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
}
