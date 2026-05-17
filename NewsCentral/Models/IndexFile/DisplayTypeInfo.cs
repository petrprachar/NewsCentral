namespace NewsCentral.Models.IndexFile
{
    /// <summary>
    /// Display type flags for content
    /// Indicates which display modes this content supports
    /// </summary>
    public class DisplayTypeInfo
    {
        /// <summary>
        /// Display as "News of the Week" content
        /// </summary>
        public bool IsNewsOfWeek { get; set; }

        /// <summary>
        /// Display as desktop wallpaper
        /// </summary>
        public bool IsWallpaper { get; set; }

        /// <summary>
        /// Display on logon screen
        /// </summary>
        public bool IsLogonScreen { get; set; }
    }
}
