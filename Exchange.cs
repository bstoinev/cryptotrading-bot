using CryptoArbiter.Core.Dealing;
using CryptoArbiter.Core.Extensibility.ExchangeOrderExtensions;
using CryptoArbiter.Core.Gdax;
using CryptoArbiter.Core.Gdax.WssFeed;
using CryptoArbiter.Core.Monitoring;
using CryptoArbiter.Core.Poloniex;
using CryptoArbiter.Core.Poloniex.WssFeed;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CryptoArbiter.Core
{
    public class Exchange<TDealer, TMonitor, TExchangeOrder> : IDisposable, IExchange<TDealer, TMonitor, TExchangeOrder>
        where TDealer : class, IExchangeDealer
        where TMonitor : class, IExchangeMonitor
        where TExchangeOrder : class, IExchangeOrder, new()
    {
        #region IDisposable support

        public void Dispose()
        {
            Monitor.Off();

            var a = Agent as IDisposable;

            if (a != null)
            {
                a.Dispose();
            }
        }

        #endregion

        private readonly IExchangeAgent Agent = null;

        private readonly IExchangeFeed Feed = null;

        private readonly object orderReceivedLocker = new object();

        protected readonly List<IExchangeOrder> OrderBook = new List<IExchangeOrder>();

        protected readonly List<IExchangeOrder> PlacedOrders = new List<IExchangeOrder>();

        public event EventHandler<OrderReceiveEventArgs> OrderReceived;

        public event EventHandler<TradeEventArgs> PrivateTradeCompleted;
        public event EventHandler<OrderDismissedEventArgs> OrderDismissed;

        public Exchange(string name)
        {
            Name = name;

            var monitorType = typeof(TMonitor);

            IExchangeMonitor theMonitor = null;
            IExchangeDealer theDealer = null;

            if (monitorType == typeof(PoloniexMonitor))
            {
                Agent = new PoloniexAgent();
                Feed = new PoloniexFeed();

                theDealer = new PoloniexDealer((PoloniexAgent)Agent);
                theMonitor = new PoloniexMonitor((PoloniexAgent)Agent, Feed);
            }
            else if (monitorType == typeof(GdaxMonitor))
            {
                Agent = new GdaxAgent();
                Feed = new GdaxFeed();

                theDealer = new GdaxDealer((GdaxAgent)Agent);
                theMonitor = new GdaxMonitor((GdaxAgent)Agent, (GdaxFeed)Feed);
            }
            else
            {
                throw new ArgumentException("Unknown monitor type.", nameof(TMonitor));
            }

            Dealer = (TDealer)theDealer;

            Monitor = (TMonitor)theMonitor;
            Monitor.OrderReceivedCallback = ReceivingOrder;
            Monitor.OnTradeCallback = TradeOccurred;
            Monitor.OnOrderDismissed = OrderEjected;
        }

        private void ReceivingOrder(IExchangeOrder eo)
        {
            var ea = new OrderReceiveEventArgs(eo);
            //ea.HasChanged = existingOrder != null && existingOrder.Price != eo.Price && existingOrder.Size != eo.Size;

            OrderReceived?.Invoke(this, ea);

            lock (orderReceivedLocker)
            {
                var existingOrder = OrderBook.FirstOrDefault(o => o.IsLike(eo));

                if (existingOrder != null)
                {
                    OrderBook.Remove(existingOrder);
                }

                OrderBook.Add(eo);
            }
        }

        private void OrderEjected(IOrderDismissedArgs args)
        {
            OrderDismissed?.Invoke(this, new OrderDismissedEventArgs(args.OrderId, args.Reason, args.Timestamp));
        }

        private void TradeOccurred(ITradeArgs tradeParameters)
        {
            var makerOrder = PlacedOrders.FirstOrDefault(o => o.Id.Equals(tradeParameters.MakerOrderId));
            var takerOrder = PlacedOrders.FirstOrDefault(o => o.Id.Equals(tradeParameters.TakerOrderId));

            var isPrivateTrade = makerOrder != null || takerOrder != null;

            if (isPrivateTrade)
            {
                var ea = new TradeEventArgs(tradeParameters);
                PrivateTradeCompleted?.Invoke(this, ea);

                PlacedOrders.RemoveAll(o => o.Equals(makerOrder ?? takerOrder));
            }

        }

        public string ApiKey { get; protected set; }

        public string ApiSecret { get; protected set; }

        protected TDealer Dealer { get; set; }

        protected TMonitor Monitor { get; set; }

        public string Name { get; private set; }

        public string Passphrase { get; protected set; }

        public decimal? GetTick(TradingInstrument instrument)
        {
            return Dealer.GetTickSize(instrument);
        }

        public void ApplyConfig()
        {
            Agent.ReadConfig();
            Monitor.Configure();
        }

        public TExchangeOrder CopyOrder(IExchangeOrder source)
        {
            var result = new TExchangeOrder
            {
                Id = source.Id,
                Instrument = source.Instrument,
                OrderType = source.OrderType,
                PlacedOn = source.PlacedOn,
                Price = source.Price,
                Size = source.Size,
                Status = source.Status
            };


            return result;
        }

        public TExchangeOrder CreateOrder()
        {
            return new TExchangeOrder();
        }

        public override string ToString()
        {
            return Name;
        }

        public IEnumerable<IExchangeOrder> GetCachedOrders(Func<IExchangeOrder, bool> predicate = null)
        {
            lock (orderReceivedLocker)
            {
                var cache = predicate == null ? OrderBook : OrderBook.Where(predicate);

                return cache.ToArray();
            }
        }

        public Task StartObservation()
        {
            return Monitor.On();
        }

        public void StopObservation()
        {
            Monitor.Off();
        }

        public async Task<IExchangeOrder> PlaceOrder(IExchangeOrder order)
        {
            var poloniex = this as Exchange<PoloniexDealer, PoloniexMonitor, PoloniexOrder>;

            if (poloniex != null)
            {
                // This is Poloniex
                // Convert market order to "marketable limit" order

                var poloOrder = (PoloniexOrder)order;

                if (poloOrder.OrderType.HasFlag(ExchangeOrderType.Market))
                {
                    // Market orders are unsupported on Poloniex.
                    // Convert it to a marketable limit order

                    poloOrder.OrderType &= ~ExchangeOrderType.Market;
                    poloOrder.OrderType |= ExchangeOrderType.Limit;

                    if (poloOrder.Side == DealType.Ask)
                    {
                        var highestBidOrder = OrderBook.Where(o => o.Side == DealType.Bid).Aggregate((c, n) => n.Price > c.Price ? n : c);
                        poloOrder.Price = highestBidOrder.Price;
                    }
                    else if (poloOrder.Side == DealType.Bid)
                    {
                        var lowestAskOrder = OrderBook.Where(o => o.Side == DealType.Ask).Aggregate((c, n) => n.Price < c.Price ? n : c);
                        poloOrder.Price = lowestAskOrder.Price;
                    }
                    else
                    {
                        throw new NotSupportedException($"Unsupported deal type: {poloOrder.Side}");
                    }
                }
            }

            var placedOrder = await Dealer.PlaceOrder(order);

            if (placedOrder != null)
            {
                PlacedOrders.Add(placedOrder);
            }

            return placedOrder;
        }

        public IFeeInfo GetFee(TradingInstrument instrument)
        {
            return Dealer.GetFeeInfo(instrument);
        }

        public async Task<bool> CancelOrder(IExchangeOrder order)
        {
            return await Dealer.CancelOrder(order);
        }
    }
}
