using NewsCentral.Security;

namespace NewsViewer.Configuration;

public class ViewerConfiguration
{
    public string Company { get; set; } = "Contoso";
    public string ApplicationName { get; set; } = "NewsCentral";
    public string CacheRootPath { get; set; } = @"C:\ProgramData\NewsCentral";
    public bool BypassShowOnceCheck { get; set; } = false;
    public HmacOptions Hmac { get; set; } = new();
}
