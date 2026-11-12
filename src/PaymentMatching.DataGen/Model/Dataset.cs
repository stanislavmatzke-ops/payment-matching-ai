namespace PaymentMatching.DataGen.Model;

public sealed record Dataset(
    IReadOnlyList<Customer> Customers,
    IReadOnlyList<Invoice> Invoices,
    IReadOnlyList<BankTransaction> Transactions,
    IReadOnlyList<ExpectedMatch> Expected);
