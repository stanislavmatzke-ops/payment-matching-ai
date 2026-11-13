using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;

namespace PaymentMatching.Web;

/// <summary>
/// Keeps the Data Protection key ring in PostgreSQL (<c>data_protection_keys</c>). In Azure the web app
/// scales to zero; with keys only in the container every restart would sign everyone out and break the
/// antiforgery tokens of open pages (ADR 0016). Anyone who can read this table can already read all data.
/// </summary>
internal sealed class DbXmlRepository(DbContextOptions<OperationsDbContext> options) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var db = new OperationsDbContext(options);
        return db.DataProtectionKeys.AsNoTracking().OrderBy(k => k.Id).Select(k => k.Xml).AsEnumerable()
            .Select(xml => XElement.Parse(xml))
            .ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var db = new OperationsDbContext(options);
        db.DataProtectionKeys.Add(new DataProtectionKeyRow { Name = friendlyName, Xml = element.ToString(SaveOptions.DisableFormatting) });
        db.SaveChanges();
    }
}

internal static class DataProtectionKeys
{
    /// <summary>One application name, so every replica and revision reads the same key ring.</summary>
    public const string ApplicationName = "payment-matching-ai";

    public static IServiceCollection AddDatabaseKeyRing(this IServiceCollection services, DbContextOptions<OperationsDbContext> options)
    {
        services.AddDataProtection().SetApplicationName(ApplicationName);
        services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(o =>
            o.XmlRepository = new DbXmlRepository(options));
        return services;
    }
}
