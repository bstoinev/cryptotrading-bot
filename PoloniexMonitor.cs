using CryptoArbiter.Core.Monitoring;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using PoloniexWebSocketsApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace CryptoArbiter.Core.Poloniex
{
    public class PoloniexMonitor : ExchangeMonitorBase, IExchangeMonitor
    {
        private readonly PoloniexAgent Agent = null;

        protected override Logger Log { get; set; } = LogManager.GetCurrentClassLogger();

        public PoloniexMonitor(PoloniexAgent agent, IExchangeFeed feed) : base("Poloniex")
        {
            Agent = agent;
            Feed = feed;
        }

        public Action<IExchangeOrder> OrderReceivedCallback { get; set; }

        protected override IExchangeFeedMessage BuildFeedSubscriptionMessage()
        {
            throw new NotImplementedException();
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
                Log.Error($"Failed to retrieve the {subscription.Instrument} order book with the following exception: ");
                Log.Error(ex.ToString().ToString().Replace(Environment.NewLine, ">>"));
            }

            if (!string.IsNullOrEmpty(response) && OrderReceivedCallback != null)
            {
                Task.Run(() => ProcessOrderBookResponse(response, subscription), Interruptor.Token);
            }

            return response;
        }

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
                isFrozen = string.Empty,
                sequence = string.Empty
            };

            try
            {
                orderBook = JsonConvert.DeserializeAnonymousType(response, orderBook);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to deserialize the order book response with the following exceptions: {Environment.NewLine}");
                Log.Error(ex.ToString().Replace(Environment.NewLine, ">>"));
                Log.Info($"The response string was: {response}");
                orderBook = null;
            }

            if (orderBook != null)
            {
                var isFrozen = -1;

                var theMarketIsFrozen = int.TryParse(orderBook.isFrozen, out isFrozen) && isFrozen != 0;

                if (theMarketIsFrozen)
                {
                    Log.Warn($"Currently the {currentSubscription.Instrument} market is frozen at Poloniex. Skipping.");
                }
                else
                {
                    if (orderBook?.asks != null)
                    {
                        var lowestAsk = orderBook.asks.Aggregate((c, n) => n[0] < c[0] ? n : c);

                        var sellOrder = new PoloniexOrder
                        {
                            Instrument = currentSubscription.Instrument,
                            OrderType = ExchangeOrderType.Sell,
                            Price = lowestAsk[0],
                            Size = lowestAsk[1]
                        };

                        OrderReceivedCallback(sellOrder);
                    }

                    if (orderBook?.bids != null)
                    {
                        var highestBid = orderBook.bids.Aggregate((c, n) => n[0] > c[0] ? n : c);

                        var buyOrder = new PoloniexOrder
                        {
                            Instrument = currentSubscription.Instrument,
                            OrderType = ExchangeOrderType.Buy,
                            Price = highestBid[0],
                            Size = highestBid[1]
                        };

                        OrderReceivedCallback(buyOrder);
                    }
                }
            }
        }

        public void Configure()
        {
            ReadConfig();
        }


        private void Feed_MessageArrived(JsonSerializer serializer, object message)
        {
            //var jo = JObject.Parse(message.ToString());


        }

    }
}
