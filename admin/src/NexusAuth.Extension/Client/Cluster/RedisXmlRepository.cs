using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using StackExchange.Redis;

namespace NexusAuth.Extension;

/// <summary>把 Data Protection 密钥 XML 追加到 Redis 列表，使所有实例读取同一密钥环。</summary>
internal sealed class RedisXmlRepository(Func<IDatabase> database, RedisKey key) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements() =>
        database().ListRange(key).Select(value => XElement.Parse((string)value!)).ToArray();

    public void StoreElement(XElement element, string friendlyName) =>
        database().ListRightPush(key, element.ToString(SaveOptions.DisableFormatting));
}
