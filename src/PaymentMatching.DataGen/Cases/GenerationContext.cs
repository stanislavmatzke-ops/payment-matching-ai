using System.Globalization;
using PaymentMatching.DataGen.Model;

namespace PaymentMatching.DataGen.Cases;

/// <summary>Shared mutable state of one generator run: random source, customers and issued invoices.</summary>
internal sealed class GenerationContext(Random rng, GeneratorOptions options)
{
    private readonly List<Customer> _customers = [];
    private readonly List<Invoice> _invoices = [];
    private int _nextSequence = options.FirstInvoiceSequence;

    public Random Rng { get; } = rng;

    public int Year => options.ReferenceDate.Year;

    public DateOnly ReferenceDate => options.ReferenceDate;

    public string PaymentIdPrefix => options.PaymentIdPrefix;

    public IReadOnlyList<Customer> Customers => _customers;

    public IReadOnlyList<Invoice> Invoices => _invoices;

    public Customer AddCustomer()
    {
        var id = options.CustomerIdPrefix + (_customers.Count + 1).ToString("D3", CultureInfo.InvariantCulture);
        var customer = new Customer(id, FictionalNames.Company(Rng), CzechIban.NewDemoBankIban(Rng));
        _customers.Add(customer);
        return customer;
    }

    public Customer RandomCustomer() => Rng.Pick(_customers);

    /// <summary>Issues a new CZK invoice with a gross amount between roughly 1 000 and 240 000 CZK.</summary>
    public Invoice IssueInvoice(Customer customer, decimal? amount = null)
    {
        var sequence = _nextSequence++;
        var issueDate = ReferenceDate.AddDays(-Rng.Next(10, 75));
        var invoice = new Invoice(
            InvoiceNumber: $"{Year}-{sequence}",
            VariableSymbol: VariableSymbolFor(Year, sequence),
            CustomerId: customer.CustomerId,
            Kind: Rng.Chance(0.1) ? InvoiceKind.Advance : InvoiceKind.Regular,
            IssueDate: issueDate,
            DueDate: issueDate.AddDays(14),
            Amount: amount ?? RandomGrossAmount(),
            Currency: "CZK");
        _invoices.Add(invoice);
        return invoice;
    }

    /// <summary>A booking date after the invoice was issued and not later than the reference date.</summary>
    public DateOnly BookingDateFor(Invoice invoice)
    {
        var latest = ReferenceDate.DayNumber - invoice.IssueDate.DayNumber;
        return invoice.IssueDate.AddDays(Rng.Next(1, latest + 1));
    }

    public DateOnly RandomBookingDate() => ReferenceDate.AddDays(-Rng.Next(0, 30));

    public string PrintedName(Customer customer) => FictionalNames.AsBankWouldPrint(customer.Name, Rng);

    public string RandomConstantSymbol() => Rng.Chance(0.6) ? "0308" : "";

    public static string VariableSymbolFor(int year, int sequence) =>
        (year * 1_000_000L + sequence).ToString(CultureInfo.InvariantCulture);

    private decimal RandomGrossAmount()
    {
        var net = Rng.Next(800, 200_000) + (Rng.Next(0, 100) / 100m);
        return Math.Round(net * 1.21m, 2, MidpointRounding.AwayFromZero);
    }
}
