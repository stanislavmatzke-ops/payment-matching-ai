using System.Globalization;
using PaymentMatching.Data;

namespace PaymentMatching.Web;

/// <summary>
/// The payments list's filter, search, sort and page, from the query string. Every value is checked against
/// a fixed set or a length limit; the search text is only compared in memory and written back as text.
/// Paging runs on the server over the loaded sandbox (hundreds of payments at most), so no script is needed.
/// </summary>
public sealed record PaymentQuery(string? State, string? By, string? Search, string Sort, bool Descending, int Page)
{
    public const int PageSize = 25;
    public const int MaxSearch = 100;

    /// <summary>Who decided: the layers a person can filter on.</summary>
    public static readonly string[] Deciders = ["ai", "rule", "person", "policy", "none"];

    public static readonly string[] Sorts = ["date", "amount", "payer", "id"];

    public static PaymentQuery Default { get; } = new(null, null, null, "date", true, 1);

    public static PaymentQuery From(string? state, string? by, string? q, string? sort, string? dir, int? page) => new(
        state is not null && PaymentState.All.Contains(state) ? state : null,
        by is not null && Deciders.Contains(by) ? by : null,
        string.IsNullOrWhiteSpace(q) ? null : q.Trim() is var t && t.Length > MaxSearch ? t[..MaxSearch] : q.Trim(),
        sort is not null && Sorts.Contains(sort) ? sort : "date",
        dir != "asc",
        page is > 0 ? page.Value : 1);

    /// <summary>The kind of decider of a list item, as in <see cref="Deciders"/>.</summary>
    public static string DeciderOf(string? decidedBy) => decidedBy switch
    {
        null => "none",
        var v when v.StartsWith("ai:", StringComparison.Ordinal) => "ai",
        var v when v.StartsWith("rule:", StringComparison.Ordinal) => "rule",
        var v when v.StartsWith("person", StringComparison.Ordinal) => "person",
        _ => "policy",
    };

    public PaymentListPage Apply(IReadOnlyList<PaymentListItem> all)
    {
        IEnumerable<PaymentListItem> items = all;
        if (State is not null)
        {
            items = items.Where(p => p.State == State);
        }

        if (By is not null)
        {
            items = items.Where(p => DeciderOf(p.DecidedBy) == By);
        }

        if (Search is { } s)
        {
            items = items.Where(p => Matches(p, s));
        }

        items = (Sort, Descending) switch
        {
            ("amount", false) => items.OrderBy(p => p.Amount),
            ("amount", true) => items.OrderByDescending(p => p.Amount),
            ("payer", false) => items.OrderBy(p => p.CounterpartyName, StringComparer.CurrentCultureIgnoreCase),
            ("payer", true) => items.OrderByDescending(p => p.CounterpartyName, StringComparer.CurrentCultureIgnoreCase),
            ("id", false) => items.OrderBy(p => p.Id, StringComparer.Ordinal),
            ("id", true) => items.OrderByDescending(p => p.Id, StringComparer.Ordinal),
            (_, false) => items.OrderBy(p => p.BookingDate).ThenBy(p => p.Id, StringComparer.Ordinal),
            _ => items.OrderByDescending(p => p.BookingDate).ThenByDescending(p => p.Id, StringComparer.Ordinal),
        };

        var filtered = items.ToList();
        var pages = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
        var page = Math.Min(Page, pages);
        return new PaymentListPage(filtered.Skip((page - 1) * PageSize).Take(PageSize).ToList(), filtered.Count, all.Count, page, pages, this with { Page = page });
    }

    /// <summary>Payment id, payer, VS, message or amount (as typed, "1500" or "1500.00").</summary>
    private static bool Matches(PaymentListItem p, string s) =>
        p.Id.Contains(s, StringComparison.OrdinalIgnoreCase)
        || p.CounterpartyName.Contains(s, StringComparison.CurrentCultureIgnoreCase)
        || p.VariableSymbol.Contains(s, StringComparison.Ordinal)
        || p.Message.Contains(s, StringComparison.CurrentCultureIgnoreCase)
        || p.Amount.ToString("0.00", CultureInfo.InvariantCulture).StartsWith(s.Replace(',', '.').Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);

    /// <summary>The list's address with this query, optionally changed (for sort headers and the pager).</summary>
    public string Url(string? sort = null, bool? descending = null, int? page = null)
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        Add("state", State);
        Add("by", By);
        Add("q", Search);
        var s = sort ?? Sort;
        var d = descending ?? Descending;
        if (s != "date" || !d)
        {
            Add("sort", s);
            Add("dir", d ? "desc" : "asc");
        }

        var p = page ?? Page;
        if (p > 1)
        {
            Add("page", p.ToString(CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "/payments" : "/payments?" + string.Join('&', parts);
    }

    /// <summary>A column header's link: the same column flips the direction, another starts descending (dates, amounts) or ascending (text).</summary>
    public string SortUrl(string column) =>
        Url(column, column == Sort ? !Descending : column is "date" or "amount", page: 1);
}

/// <summary>One page of the payments list and where it is in the whole.</summary>
public sealed record PaymentListPage(IReadOnlyList<PaymentListItem> Items, int Matching, int Total, int Page, int Pages, PaymentQuery Query);
