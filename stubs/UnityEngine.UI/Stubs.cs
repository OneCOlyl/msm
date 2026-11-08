namespace UnityEngine.UI
{
    public class Graphic : UnityEngine.MonoBehaviour { public bool raycastTarget { get; set; } public UnityEngine.RectTransform rectTransform { get; set; } }
    public class Text : Graphic { public string text { get; set; } }
    public class Image : Graphic { public float fillAmount { get; set; } }
    public class Selectable : UnityEngine.MonoBehaviour { }
    public class Button : Selectable { }
    public class InputField : Selectable
    {
        public enum ContentType { Standard, Autocorrected, IntegerNumber, DecimalNumber, Alphanumeric, Name, EmailAddress, Password, Pin, Custom }
        public ContentType contentType { get; set; }
        public int characterLimit { get; set; }
        public string text { get; set; }
    }
}
