namespace PaymentMatching.DataGen.Tests;

public class CzechIbanTests
{
    [Fact]
    public void Generated_iban_has_valid_checksum_and_account_number()
    {
        var rng = new Random(1);

        for (var i = 0; i < 100; i++)
        {
            var iban = CzechIban.NewDemoBankIban(rng);
            Assert.True(CzechIban.IsValid(iban), iban);
            Assert.True(CzechIban.IsValidAccountNumber(iban[14..]), iban);
        }
    }

    [Theory]
    [InlineData("CZ00")]
    [InlineData("SK3112000000198742637541")]
    [InlineData("CZ00999000000000000000AB")]
    public void Malformed_iban_is_invalid(string iban) => Assert.False(CzechIban.IsValid(iban));

    [Fact]
    public void Changed_digit_breaks_checksum()
    {
        var iban = CzechIban.NewDemoBankIban(new Random(3));
        var last = iban[^1] == '9' ? '0' : (char)(iban[^1] + 1);

        Assert.False(CzechIban.IsValid(iban[..^1] + last));
    }
}
