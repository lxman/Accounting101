using System.Globalization;

namespace Accounting101.Angular.Server.Attributes;

[AttributeUsage(AttributeTargets.Assembly)]
internal class BuildTimeAttribute : Attribute
{
    public DateTime BuildTime { get; } = DateTime.UtcNow;

    public BuildTimeAttribute(string value)
    {
        BuildTime = DateTime.ParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None);
    }
}
