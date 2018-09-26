using CryptoArbiter.Core.Configuration;
using NLog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoArbiter.Core.Monitoring
{
    public abstract class ExchangeMonitorBase
    {
        private readonly string ExchangeTag = null;

        protected readonly CancellationTokenSource Interruptor = new CancellationTokenSource();

        protected readonly IList<QuoteSubscription> Subscriptions = new List<QuoteSubscription>();

        protected IExchangeFeed Feed = null;

        /// <summary>
        /// Perform the actual quote download form the respective exchange.
        /// </summary>
        /// <param name="subscription">Currently processed QuoteSubscription.</param>
        /// <returns></returns>
        /// <remarks>
        /// The method is called by the base class whenever the configured (though maxRequestsPerMinute) delay between two requests elapses.
        /// The workflow is to download the data and call the ReceiveQuote method.
        /// However, because the execution time of the Pulse() method adds to the total request execution time, 
        /// it is advised to run the response deserialization and the call to the ReceiveQuote on a separate thresad.
        /// 
        /// In case where an exchange returns bulk data (ticker), execute the download request once.
        /// The appropriate time for download might be when the <paramref name="subscription"/> is the first subscription in the Subscriptions list.
        /// Pay attention to the fact that even if the mehod receives the currently processing quote, 
        /// the inheritor still has access to all the quote subscriptions through the Subscriptions property.
        /// Once the reponse is deserialized, call ReceiveQuote() multiple time, once for each quote of interests. 
        /// For more details, see PoloniexMonitor.Pulse() implementation.
        /// </remarks>
        protected abstract Task<string> Pulse(QuoteSubscription subscription);

        protected ExchangeMonitorBase(string exchangeTag)
        {
            if (string.IsNullOrEmpty(exchangeTag))
            {
                throw new ArgumentNullException(nameof(exchangeTag));
            }

            ExchangeTag = exchangeTag;
        }

        public Action<IOrderDismissedArgs> OnOrderDismissed
        {
            get => Feed.OnOrderDismissed;
            set => Feed.OnOrderDismissed = value;
        }

        public Action<IOrderbookUpdateArgs> OnOrderbookUpdate
        {
            get => Feed.OnOrderbookUpdate;
            set => Feed.OnOrderbookUpdate = value;
        }

        public Action<ITradeArgs> OnTradeCallback
        {
            get => Feed.OnTrade;
            set => Feed.OnTrade = value;
        }

        protected abstract Logger Log { get; set; }

        /// <summary>
        /// true when the monitor is on; otherwise false;
        /// </summary>
        public bool Busy { get; private set; }

        /// <summary>
        /// The alloted time for one request by the exchange's request limit policy.
        /// </summary>
        public int RequestPeriod { get; private set; }

        protected abstract IExchangeFeedMessage BuildFeedSubscriptionMessage();

        protected void Subscribe()
        {
            var message = BuildFeedSubscriptionMessage();
           
            try
            {
                Feed.Subscribe(message);
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString(), $"Failed to send the {message.GetType().Name} message.");
            }
        }

        public void Off()
        {
            if (Busy)
            {
                Interruptor.Cancel();

                SpinWait.SpinUntil(() => !Busy, RequestPeriod * 10);

                if (Busy)
                {
                    Log.Warn($"Failed to shutdown the {GetType().Name} gracefully.");
                }
            }
        }

        public Task On()
        {
            if (Busy)
            {
                throw new InvalidOperationException("The monitoring is already in progress.");
            }

            var result = Task.Run(async () =>
            {
                Busy = true;

                try
                {
                    await Run();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "The current monitor automatically switched off.");
                }

                Busy = false;
            });

            return result;
        }

        public void ReadConfig()
        {
            var cacs = ConfigurationManager.GetSection("cryptoArbiter") as CryptoArbiterConfigSection;

            if (cacs == null)
            {
                throw new ConfigurationErrorsException("Missing configuration.");
            }

            var defaultSubscriptions = cacs.DefaultSubscriptions.Cast<QuoteSubscriptionConfigElement>();

            var cfg = cacs.Exchanges[ExchangeTag];

            RequestPeriod = (int)((1D / cfg.Monitor.MaxRequestPerMinute) * 60 * 1000);

            var subscribeTo = cfg.Monitor.Subscriptions.Cast<QuoteSubscriptionConfigElement>().Union(defaultSubscriptions);

            var subscriptionsList = Subscriptions as List<QuoteSubscription>;

            subscriptionsList.AddRange(subscribeTo.Select(s => (QuoteSubscription)s).ToArray());
        }

        private async Task Run()
        {
            Feed.Open();

            var chrono = new Stopwatch();
            
            while (!Interruptor.IsCancellationRequested)
            {
                // TODO:  Update subscription list
                //  Implement "Active" and "Pending" subscriptions, so the list could be updated here.
                //  When Subscribe() mehod is called, append the subscription to the "PendingSubscriptions" list.
                //  Now ittereate over the "ActiveSubscriptions" list
                foreach (var subscriptionX in Subscriptions)
                {
                    chrono.Restart();

                    try
                    {
                        await Pulse(subscriptionX);
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Debug("Monitoring has been cancelled.");
                        break;
                    }

                    chrono.Stop();

                    if (chrono.Elapsed.Milliseconds < RequestPeriod)
                    {
                        var delay = RequestPeriod - chrono.Elapsed.Milliseconds;

                        Log.Debug($"The request finished earlier ({chrono.ElapsedMilliseconds}ms) than the configured request period ({RequestPeriod}ms).");
                        Log.Debug($"Delaying next request with {delay}ms.");

                        try
                        {
                            await Task.Delay(delay, Interruptor.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            Log.Debug("Monitoring cancelled.");
                        }
                    }
                }
            }

            Feed.Close();
        }

        public void Subscribe(QuoteSubscription subscriptionDetails)
        {
            if (Busy)
            {
                throw new InvalidOperationException($"Unable to add a subscription as monitoring is in progress. Call the {nameof(Off)}() method first.");
            }
            else if (subscriptionDetails == null)
            {
                throw new ArgumentNullException(nameof(subscriptionDetails));
            }

            if (!Subscriptions.Contains(subscriptionDetails))
            {
                Subscriptions.Add(subscriptionDetails);
            }
        }

        public void Unsubscribe(QuoteSubscription subscriptionDetails)
        {
            if (Busy)
            {
                throw new InvalidOperationException($"Cannot unsubscribe as monitoring is in progress. Call the {nameof(Off)}() method first.");
            }
            else if (subscriptionDetails == null)
            {
                throw new ArgumentNullException("which");
            }

            if (Subscriptions.Contains(subscriptionDetails))
            {
                Subscriptions.Remove(subscriptionDetails);
            }
        }

    }
}
