namespace UnityEngine.UI
{
    public class Graphic : UnityEngine.MonoBehaviour { }
    public class Text : Graphic { public string text { get; set; } }
    public class Image : Graphic { }
    public class Selectable : UnityEngine.MonoBehaviour { }
    public class Button : Selectable { }
    public class InputField : Selectable { }
}
