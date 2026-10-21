namespace UnityEngine
{
    public class Transform { public Vector3 position = new Vector3 { y = 1.1f }; }
    public struct Vector4 { public float x,y,z,w; public Vector4(float x,float y,float z,float w) { this.x=x; this.y=y; this.z=z; this.w=w; } }
    public class Sprite
    {
        public string name;
        public Rect textureRect = new Rect(16, 32, 80, 60);
        public Vector4 border = new Vector4(5, 6, 7, 8);
        public Texture2D texture = new Texture2D(128, 128);
    }
    public static class Resources
    {
        public static readonly Font Liberation = new Font { name = "LiberationSans" }, Bold = new Font { name = "LiberationSans-Bold" };
        public static Font[] Fonts = { Liberation, Bold };
        public static Sprite[] Sprites = { new Sprite { name = "newpaper" }, new Sprite { name = "newmenubutton2" } };
        public static int Searches;
        public static T[] FindObjectsOfTypeAll<T>() { Searches++; return typeof(T) == typeof(Sprite) ? (T[])(object)Sprites : (T[])(object)Fonts; }
        public static T GetBuiltinResource<T>(string name) where T : new() => new T();
    }
}
