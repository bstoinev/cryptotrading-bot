using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CryptoArbiter.Core
{
    public abstract class ExchangeOrder : IExchangeOrder
    {
        public static ExchangeOrder Empty()
        {
            return new ExchangeOrder();
        }

        public ExchangeOrder()
        {

        }

        public ExchangeOrder(ExchangeOrderType orderType)
        {
            OrderType = orderType;
        }

        public object Id { get; set; }

        public TradingInstrument Instrument { get; set; }

        public ExchangeOrderType OrderType { get; set; }

        public DateTime? PlacedOn { get; set; }

        public decimal Price { get; set; }

        public decimal Size { get; set; }

        public DealType Side
        {
            get
            {
                var result = DealType.Unknown;

                if (OrderType.HasFlag(ExchangeOrderType.Buy))
                {
                    result |= DealType.Bid;
                }
                else if (OrderType.HasFlag(ExchangeOrderType.Sell))
                {
                    result |= DealType.Ask;
                }

                return result;
            }
        }

        public ExchangeOrderStatus Status { get; set; }


        public override string ToString()
        {
            return $"({OrderType}) {Instrument}, Size:{Size}, Price:{Price}";
        }
    }
}
