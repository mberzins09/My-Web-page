namespace MartinsWeb.Models
{
    public class LgtfEntry
    {
        public int    Id           { get; set; }
        public string Title        { get; set; } = "";
        public string? Body        { get; set; }          // HTML/markdown text
        public string? ImageBase64 { get; set; }          // base64-encoded image
        public string? ImageMime   { get; set; }          // e.g. "image/jpeg"
        public int    DisplayOrder { get; set; } = 0;
        public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    }
}
