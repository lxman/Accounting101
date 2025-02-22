namespace Accounting101;

public class Program
{
    public static void Main()
    {
        WebApplication app = new RegisterServices().WebApplication;

        _ = new AppSetup(app);
    }
}