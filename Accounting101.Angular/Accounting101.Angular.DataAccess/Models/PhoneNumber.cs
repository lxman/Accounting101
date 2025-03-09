namespace Accounting101.Angular.DataAccess.Models;

public class PhoneNumber
{
    public string Number { get; set; }

    public PhoneNumber(string number)
    {
        Number = number;
    }
}