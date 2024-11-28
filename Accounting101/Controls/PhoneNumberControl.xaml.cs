using System.Windows.Controls;
using PhoneNumbers;

namespace Accounting101.Controls
{
    public partial class PhoneNumberControl : UserControl
    {
        public string LabelContent { get; set; } = "Phone Number:";

        public string PhoneNumber
        {
            get => _phoneNumber;
            set
            {
                try
                {
                    string sanitized = value.Replace("+", string.Empty).Replace("(", string.Empty).Replace(")", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
                    PhoneNumber? parsed = PhoneNumberUtil.Parse(sanitized, "US");
                    _phoneNumber = PhoneNumberUtil.IsValidNumber(parsed)
                        ? PhoneNumberUtil.Format(parsed, PhoneNumberFormat.NATIONAL)
                        : value;
                }
                catch
                {
                    _phoneNumber = value;
                }
            }
        }

        private static readonly PhoneNumberUtil PhoneNumberUtil = PhoneNumberUtil.GetInstance();
        private string _phoneNumber = string.Empty;

        public PhoneNumberControl()
        {
            InitializeComponent();
        }
    }
}
