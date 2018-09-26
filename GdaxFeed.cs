using CryptoArbiter.Core.Monitoring;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Diagnostics;
using WebSocketSharp;

namespace CryptoArbiter.Core.Gdax.WssFeed
{
    public class GdaxFeed : ExchangeWsSharpFeedBase, IExchangeFeed
    {
        private static NLog.Logger Log = LogManager.GetCurrentClassLogger();

        private GdaxSubscribeFeedMessage CurrentSubscription = null;

        public Action<IOrderbookUpdateArgs> OnOrderbookUpdate { get; set; }
        public Action OnStart { get; set; }
        public Action OnStop { get; set; }
        public Action<ITradeArgs> OnTrade { get; set; }
        public Action<IOrderDismissedArgs> OnOrderDismissed { get; set; }

        protected override void OnError(object sender, ErrorEventArgs e)
        {
            base.OnError(sender, e);
        }

        protected override void OnClose(object sender, CloseEventArgs e)
        {
            Log.Warn($"The web socket is closing with code {e.Code}");
            Log.Warn($"Close reason: {e.Reason}");

            OnStop?.Invoke();
        }

        protected override void OnMessage(object sender, MessageEventArgs e)
        {
            var response = JObject.Parse(e.Data);

            switch (response["type"].ToString())
            {
                case "done":
                    if (OnOrderDismissed != null)
                    {
                        try
                        {
                            var arg = response.ToObject<GdaxDoneFeedMessage>();
                            OnOrderDismissed.Invoke(arg);
                        }
                        catch (JsonSerializationException ex)
                        {
                            Log.Error(ex, $"Failed to deserialize the response into {nameof(GdaxDoneFeedMessage)}");
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, $"An unexpected exception occured durring {nameof(OnOrderDismissed)}");
                        }
                    }
                    break;
                case "match": // A trade occurred between two orders
                    if (OnTrade != null)
                    {
                        GdaxTradeFeedMessage arg = null;

                        try
                        {
                            arg = response.ToObject<GdaxTradeFeedMessage>();
                            OnTrade.Invoke(arg);
                        }
                        catch (JsonSerializationException jex)
                        {
                            Log.Error(jex, $"Failed to deserialize the response into {nameof(GdaxTradeFeedMessage)}");
#if DEBUG
                            throw;
#endif
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, $"An unexpected exception occured durring {nameof(OnTrade)}");
#if DEBUG
                            throw;
#endif
                        }
                    }

                    break;
                case "open": // The order is now open on the order book
                    Debug.Print("An order has been placed on GDAX");
                    break;
                case "subscriptions": // List of channels you are subscribed to
                    CurrentSubscription = response.ToObject<GdaxSubscribeFeedMessage>();
                    break;
                default:
                    break;
            }
        }

        protected override void OnOpen(object sender, EventArgs e)
        {
            OnStart?.Invoke();
        }

        public void Open()
        {
            // TODO: Add config
            const string config_TargetUrl = "wss://ws-feed.gdax.com";

            Start(config_TargetUrl);
        }

        public void OrderBookUpdated()
        {
            throw new NotImplementedException();
        }

    }
}
