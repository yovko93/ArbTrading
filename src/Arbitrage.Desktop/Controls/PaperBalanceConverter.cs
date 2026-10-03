using System.Globalization;
using System.Windows.Data;
using Arbitrage.Contracts;

namespace Arbitrage.Desktop.Controls;

public sealed class PaperBalanceConverter : IValueConverter
{
    // Select an existing venue/currency bucket without computing or combining balances.
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as IEnumerable<PaperBalanceResponse>)?.FirstOrDefault(balance => $"{balance.Exchange}/{balance.Currency}" == parameter as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
