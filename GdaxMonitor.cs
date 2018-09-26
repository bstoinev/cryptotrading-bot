using CryptoArbiter.Core.Gdax.WssFeed;
using CryptoArbiter.Core.Monitoring;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CryptoArbiter.Core.Gdax
{
    public class GdaxMonitor : ExchangeMonitorBase, IExchangeMonitor
    {
        private readonly GdaxAgent Agent = null;

        public GdaxMonitor(GdaxAgent agent, GdaxFeed feed) : base("GDAX")
        {
            Agent = agent;

            Feed = feed;
            Feed.OnStart = () => Subscribe();
        }

        protected override Logger Log { get; set; } = LogManager.GetCurrentClassLogger();

        public Action<IExchangeOrder> OrderReceivedCallback { get; set; }

        private void ProcessOrderBookResponse(string response, QuoteSubscription currentSubscription)
        {
            var orderBook = new
            {
                asks = new[]
                {
                    new[] { 0m }
                },
                bids = new[]
                {
                    new[] { 0m }
                },
                sequence = string.Empty
            };

            try
            {
                orderBook = JsonConvert.DeserializeAnonymousType(response, orderBook);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to deserialize the order book response with the following exceptions: {Environment.NewLine}{ex.ToString()}");
                Log.Info($"The response string was: {response}");
                orderBook = null;
            }

            if (currentSubscription.Side.HasFlag(DealType.Ask) && orderBook?.asks != null)
            {
                var lowestAsk = orderBook.asks.Aggregate((c, n) => n[0] < c[0] ? n : c);

                var sellOrder = new GdaxOrder
                {
                    Instrument = currentSubscription.Instrument,
                    OrderType = ExchangeOrderType.Sell,
                    Price = lowestAsk[0],
                    Size = lowestAsk[1]
                };
                
                OrderReceivedCallback(sellOrder);
            }

            if (currentSubscription.Side.HasFlag(DealType.Bid) && orderBook?.bids != null)
            {
                var highestBid = orderBook.bids.Aggregate((c, n) => n[0] > c[0] ? n : c);

                var buyOrder = new GdaxOrder
                {
                    Instrument = currentSubscription.Instrument,
                    OrderType = ExchangeOrderType.Buy,
                    Price = highestBid[0],
                    Size = highestBid[1]
                };

                OrderReceivedCallback(buyOrder);
            }
        }

        protected override IExchangeFeedMessage BuildFeedSubscriptionMessage()
        {
            var result = new GdaxSubscribeFeedMessage()
            {
                Channels = new List<GdaxFeedChannel> { new GdaxFeedChannel { Name = "full" } },
                Instruments = Subscriptions.Select(s => s.Instrument).ToList()
            };

            return result;
        }

        protected override async Task<string> Pulse(QuoteSubscription subscription)
        {
            var response = string.Empty;

            try
            {
                response = await Agent.GetOrderBook(subscription.Instrument);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to retrieve the {subscription.Instrument} order book with the following exception:");
                Log.Error(ex.ToString().Replace(Environment.NewLine, ">>"));
            }

            if (!string.IsNullOrWhiteSpace(response) && OrderReceivedCallback != null)
            {
                var t = Task.Run(() => ProcessOrderBookResponse(response, subscription), Interruptor.Token);                
            }

            return response;
        }

        public void Configure()
        {
            ReadConfig();
        }

    }
}
