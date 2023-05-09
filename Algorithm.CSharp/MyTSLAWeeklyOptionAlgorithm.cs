#region imports
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Drawing;
using QuantConnect;
using QuantConnect.Algorithm.Framework;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Execution;
using QuantConnect.Algorithm.Framework.Risk;
using QuantConnect.Parameters;
using QuantConnect.Benchmarks;
using QuantConnect.Brokerages;
using QuantConnect.Util;
using QuantConnect.Interfaces;
using QuantConnect.Algorithm;
using QuantConnect.Indicators;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Custom;
using QuantConnect.DataSource;
using QuantConnect.Data.Fundamental;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Notifications;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Fills;
using QuantConnect.Orders.Slippage;
using QuantConnect.Scheduling;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Future;
using QuantConnect.Securities.Option;
using QuantConnect.Securities.Forex;
using QuantConnect.Securities.Crypto;
using QuantConnect.Securities.Interfaces;
using QuantConnect.Storage;
using QuantConnect.Data.Custom.AlphaStreams;
using QCAlgorithmFramework = QuantConnect.Algorithm.QCAlgorithm;
using QCAlgorithmFrameworkBridge = QuantConnect.Algorithm.QCAlgorithm;
#endregion

namespace QuantConnect.Algorithm.CSharp
{
    public class MyTSLAWeeklyOptionAlgorithm : QCAlgorithm
    {
        private Equity _equity;
        private Option _option;
        private Symbol _contract;
        private OrderTicket _entryOrder;
        private OrderTicket _exitOrder;
        private int _counter;
        private int _previousWeek;
        private bool _executedThisWeek;

        private int numOfContract = 1;
        private decimal orderPrice = 1.0m;
        private decimal stopPrice = 2.0m;

        public override void Initialize()
        {
            SetStartDate(2020, 9, 7);
            SetEndDate(2022, 8, 24);
            SetCash(100000);

            _equity = AddEquity("TSLA", Resolution.Minute);
            _equity.SetDataNormalizationMode(DataNormalizationMode.Raw);

            _option = AddOption("TSLA", Resolution.Minute);
            _option.SetFilter(UniverseFunc);

            //_option.PriceModel = OptionPriceModels.CrankNicolsonFD();

            //Schedule.On(DateRules.EveryDay(_equity.Symbol),
            //            TimeRules.AfterMarketOpen(_equity.Symbol, 5),
            //            CheckOptions);

            Schedule.On(DateRules.EveryDay(_equity.Symbol),
                        TimeRules.BeforeMarketClose(_equity.Symbol, 1),
                        MarketCloseActions);

            _contract = null;
            _entryOrder = null;
            _exitOrder = null;
            _counter = 0;
            _previousWeek = -1;
            _executedThisWeek = false;
        }

        public OptionFilterUniverse UniverseFunc(OptionFilterUniverse universe)
        {
            return universe.IncludeWeeklys()
                           //.Strikes(-1, 1)
                           .Expiration(TimeSpan.Zero, TimeSpan.FromDays(6));
        }

        public void CheckOptions()
        {
            if (!CurrentSlice.OptionChains.TryGetValue(_option.Symbol, out OptionChain chain))
            {
                Debug($"No options data for {_option.Symbol} on {Time}");
                return;
            }

            //foreach (var contract in chain)
            //{
            //    Debug($"Option contract {contract.Symbol}: expiry {contract.Symbol.ID.Date}, strike {contract.Symbol.ID.StrikePrice}, right {contract.Symbol.ID.OptionRight}, price {contract.LastPrice}, underlying {contract.UnderlyingLastPrice}");
            //}
        }

        public OptionChain GetOptionChain()
        {
            if (!CurrentSlice.OptionChains.TryGetValue(_option.Symbol, out OptionChain chain))
            {
                Debug($"No options data for {_option.Symbol} on {Time}");
                return null;
            }

            //foreach (var contract in chain)
            //{
            //    Debug($"Option contract {contract.Symbol}: expiry {contract.Symbol.ID.Date}, strike {contract.Symbol.ID.StrikePrice}, right {contract.Symbol.ID.OptionRight}, price {contract.LastPrice}, underlying {contract.UnderlyingLastPrice}");
            //}

            return chain;
        }

        private void MarketCloseActions()
        {
            Log($"{Time.ToShortDateString()}, Market Close Action.");
            if (_contract != null && IsLastTradingDayOfWeek())
            {
                if (_entryOrder != null && _entryOrder.Status == OrderStatus.Filled)
                {
                    Liquidate(_contract);
                    _contract = null;
                    _entryOrder = null;
                    _exitOrder = null;
                }
            }
        }

        public override void OnData(Slice data)
        {
            //Log($"On data.");
            //CheckOptions();
            //var chain = GetOptionChain();
            //if (chain == null)
            //{
            //    Log($"No option chain.");
            //    return;
            //}

            int currentWeek = Time.ToLocalTime().Date.IsoWeekOfYear();
            //Log($"Current week is {currentWeek}");

            if (_previousWeek != currentWeek)
            {
                _executedThisWeek = false;
                _previousWeek = currentWeek;
            }

            // only invest on the first trading day of the week
            if (_contract == null && IsFirstTradingDayOfWeek() && !_executedThisWeek)
            {
                Log($"Current time: {Time.ToShortDateString()}.");
                _contract = FindCallOptionAtPrice(data, orderPrice);
                Log($"Option contract {_contract}: expiry {_contract.ID.Date}, strike {_contract.ID.StrikePrice}, right {_contract.ID.OptionRight}, symbo {_contract.Value}");

                if (_contract != null)
                {
                    _entryOrder = MarketOrder(_contract, -1 * numOfContract);
                    _counter = 0;
                    _executedThisWeek = true;
                }
            }

            if (_contract == null)
            {
                return;
            }

            if (!Portfolio[_contract].Invested)
            {
                Log($"Not invested.");
                return;
            }

            decimal price = Securities[_contract].Price;

            if (price >= stopPrice && _counter >= 1)
            {
                Liquidate(_contract);
                _contract = null;
                _entryOrder = null;
                _exitOrder = null;
            }

            if (price >= stopPrice && _counter == 0)
            {
                _exitOrder = MarketOrder(_contract, numOfContract);
                _contract = FindCallOptionAtPrice(data, orderPrice);

                if (_contract != null)
                {
                    _entryOrder = MarketOrder(_contract, -1 * numOfContract);
                }

                _counter++;
            }
        }

        private Symbol FindCallOptionAtPrice(Slice data, decimal targetPrice)
        {
            if (!data.OptionChains.TryGetValue(_option.Symbol, out OptionChain chain))
            {
                return null;
            }

            var contracts = chain
                .Where(x => x.Right == OptionRight.Call)
                .OrderBy(x => Math.Abs(x.BidPrice - targetPrice))
                .ToList();
            Log($"Find {contracts.Count} call options.");

            return contracts.Count > 0 ? contracts[0].Symbol : null;
        }

        private bool IsFirstTradingDayOfWeek()
        {
            var currentDay = Time.ToLocalTime().Date;
            var previousDay = currentDay.AddDays(-1);

            while (!_equity.Exchange.DateIsOpen(previousDay) || previousDay.DayOfWeek == DayOfWeek.Saturday || previousDay.DayOfWeek == DayOfWeek.Sunday)
            {
                previousDay = previousDay.AddDays(-1);
            }

            return currentDay.DayOfWeek < previousDay.DayOfWeek;
        }

        private bool IsLastTradingDayOfWeek()
        {
            var currentDay = Time.ToLocalTime().Date;
            var nextDay = currentDay.AddDays(1);

            while (!_equity.Exchange.DateIsOpen(nextDay) || nextDay.DayOfWeek == DayOfWeek.Saturday || nextDay.DayOfWeek == DayOfWeek.Sunday)
            {
                nextDay = nextDay.AddDays(1);
            }

            Log($"Today is {currentDay.DayOfWeek} and next day is {nextDay.DayOfWeek} so IsLastTradingDayOfWeek is {currentDay.DayOfWeek > nextDay.DayOfWeek}.");

            return currentDay.DayOfWeek > nextDay.DayOfWeek;
        }
    }
}

public static class DateTimeExtensions
{
    public static int IsoWeekOfYear(this DateTime dateTime)
    {
        DateTime jan1 = new DateTime(dateTime.Year, 1, 1);
        DateTime dec31 = new DateTime(dateTime.Year, 12, 31);

        int daysOffset = (int)jan1.DayOfWeek - 1;
        jan1 = jan1.AddDays(-daysOffset);
        int weekNumber = (int)((dateTime - jan1).TotalDays / 7) + 1;

        if (weekNumber == 0)
        {
            // If the week number is 0, it means the date is part of the last week of the previous year.
            return new DateTime(dateTime.Year - 1, 12, 31).IsoWeekOfYear();
        }
        else if (weekNumber >= 53 && dec31.DayOfWeek != DayOfWeek.Saturday)
        {
            // If the week number is 53 and the last day of the year is not a Saturday, it means the date is part of the first week of the next year.
            return 1;
        }
        else
        {
            return weekNumber;
        }
    }
}
