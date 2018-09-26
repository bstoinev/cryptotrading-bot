using CryptoArbiter.Core.Monitoring;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using PoloniexWebSocketsApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using WebSocketSharp;

namespace CryptoArbiter.Core.Poloniex.WssFeed
{
    public class PoloniexFeed : IDisposable, IExchangeFeed
    {
        private static NLog.Logger Log = LogManager.GetCurrentClassLogger();

        private readonly IEnumerable<FieldInfo> TradingChannels = typeof(TickerSymbol)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(fi => fi.IsLiteral)
        ;

        private PoloniexChannel Feed = null;

        #region IExchangeFeedSupport

        public Action<IOrderbookUpdateArgs> OnOrderbookUpdate { get; set; }
        public Action OnStart { get; set; }
        public Action OnStop { get; set; }
        public Action<ITradeArgs> OnTrade { get; set; }

        public void Close()
        {
            if (IsOpen)
            {
                Feed.Dispose();

                IsOpen = false;
            }
        }

        public void Open()
        {
            if (IsOpen)
            {
                throw new InvalidOperationException("The feed is already open.");
            }

            IsOpen = true;

            Feed = new PoloniexChannel();

            Feed.ConnectionClosed += Feed_ConnectionClosed;
            Feed.ConnectionError += Feed_ConnectionError;
            Feed.MessageArrived += Feed_MessageArrived;

            Feed.ConnectAsync().Wait();
        }

        void IExchangeFeed.Subscribe(IExchangeFeedMessage message)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region IDisposable support

        public void Dispose()
        {
            if (Feed != null)
            {
                Feed.MessageArrived -= Feed_MessageArrived;
                Feed.ConnectionClosed -= Feed_ConnectionClosed;
                Feed.ConnectionError -= Feed_ConnectionError;

                Feed.Dispose();
            }
        }

        #endregion

        public bool IsOpen { get; private set; }
        public Action<IOrderDismissedArgs> OnOrderDismissed { get; set; }

        public void Subscribe(TradingInstrument instrument)
        {
            Subscribe(new TradingInstrument[1] { instrument });
        }

        public void Subscribe(IEnumerable<TradingInstrument> instruments)
        {
            var unsupported = new List<NotSupportedException>();
            var subscriptions = new List<PoloniexCommand>(instruments.Count());

            foreach (var pairX in instruments)
            {
                var poloniexNotation = $"{pairX.QuoteSymbol}_{pairX.BaseSymbol}";

                var targetField = TradingChannels.SingleOrDefault(s => s.Name.Equals(poloniexNotation, StringComparison.OrdinalIgnoreCase));

                if (targetField == null)
                {
                    unsupported.Add(new NotSupportedException($"The currency pair [{pairX}] is not supported."));
                }
                else
                {                    
                    subscriptions.Add(new PoloniexCommand
                    {
                        Command = PoloniexCommandType.Subscribe,
                        Channel = (int)targetField.GetRawConstantValue()
                    });
                }
            }

            if (unsupported.Any())
            {
                throw new AggregateException("There are unsupported trading instruments. See inner exception(s).", unsupported);
            }

            subscriptions.ForEach(cmd => Feed.SendAsync(cmd));
        }

        private void Feed_MessageArrived(JsonSerializer serializer, object thing)
        {
            const int trollboxChannelId = 1001;
            const int tickerChannelId = 1002;
            const int statsChannelId = 1003;
            const int heartbeatChannelId = 1010;

            var memo = JArray.Parse(thing.ToString());

            var channelId = memo[0].ToObject<int>();

            if (channelId == trollboxChannelId)
            {
                Debug.Print($"A trollbox message has been received on Poloniex channel: {thing.ToString()}");
            }
            else if (channelId == tickerChannelId)
            {
                Debug.Print($"A ticker message has been received on Poloniex channel: {thing.ToString()}");
            }
            else if (channelId == statsChannelId)
            {
                Debug.Print($"A stats message has been received on Poloniex channel: {thing.ToString()}");
            }
            else if (channelId == heartbeatChannelId)
            {
                Debug.Print("A heartbeat message has been received on Poloniex channel.");
            }
            else
            {
                // Market messages are being received

                var sequence = memo[1].ToObject<long>();

                foreach (var directive in memo[2])
                {
                    IPoloniexFeedMessage message = null;

                    var directiveType = directive[0].ToString();

                    if (directiveType == "i")
                    {
                        message = new PoloniexOrderUpdateMessage();
                    }
                    else if (directiveType == "o")
                    {
                        Log.Debug($"An order update message is being received on Poloniex: {directive}");

                        message = new PoloniexOrderUpdateMessage
                        {
                            MessageType = FeedMessageType.OrderbookUpdate
                        };                        
                    }
                    else if (directiveType == "t")
                    {
                        Debug.Print("A trade message is being received on Poloniex...");

                        message = new PoloniexTradeMessage
                        {
                            GlobalTradeId = directive[5].ToObject<int>(),
                            MessageType = FeedMessageType.Trade,
                            TradeId = directive[1].ToObject<int>()
                        };
                    }
                    else
                    {
                        throw new NotSupportedException($"Unsupported message type: {directiveType}");
                    }

                    if (message.MessageType == FeedMessageType.OrderbookUpdate || message.MessageType == FeedMessageType.Trade)
                    {
                        message.ChannelId = channelId;
                        message.Price = directive[2].ToObject<decimal>();
                        message.Sequence = sequence;
                        message.Side = directive[1].ToObject<int>() == 0 ? DealType.Ask : DealType.Bid;
                        message.Size = directive[3].ToObject<decimal>();
                    }

                    if (message.MessageType == FeedMessageType.OrderbookUpdate)
                    {
                        
                    }
                }
            }
        }

        private void Feed_ConnectionError(Exception obj)
        {
            // TODO: Log the error

            throw obj;
        }

        private void Feed_ConnectionClosed()
        {
            ((IExchangeFeed)this).OnStop?.Invoke();
        }

    }
}
