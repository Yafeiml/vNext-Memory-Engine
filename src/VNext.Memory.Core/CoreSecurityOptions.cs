namespace VNext.Memory.Core;

public sealed class CoreSecurityOptions
{
    public const string SectionName = "Security";

    public string BootstrapToken { get; set; } = string.Empty;
}
