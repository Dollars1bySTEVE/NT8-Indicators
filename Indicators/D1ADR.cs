// D1ADR Indicator Code

// Description: This indicator calculates the daily average daily range (ADR) for any specified period.

public class D1ADR
{
    private double totalRange;
    private int days;

    public D1ADR()
    {
        totalRange = 0;
        days = 0;
    }

    public void Update(double high, double low)
    {
        totalRange += (high - low);
        days++;
    }

    public double Average()
    {
        return days > 0 ? totalRange / days : 0;
    }

    public void Reset()
    {
        totalRange = 0;
        days = 0;
    }
}

// Usage Example:
// D1ADR adr = new D1ADR();
// adr.Update(highPrice, lowPrice);
// double averageRange = adr.Average();