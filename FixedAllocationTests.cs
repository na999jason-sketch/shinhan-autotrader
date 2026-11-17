using System;

namespace ShinhanNativeTrader {
    public static class FixedAllocationTests {
        static int checks;
        static void Check(bool value,string message){checks++;if(!value)throw new Exception("FixedAllocationTests: "+message);}
        static int Quantity(decimal available,decimal price=10000m,decimal feeRate=0.003m,decimal budget=1000000m,decimal fraction=0.2m) {
            return Rules.OrderQuantity(budget,available,price,fraction,feeRate);
        }
        public static int Run() {
            checks=0;
            foreach(decimal available in new[]{1000000m,800000m,600000m,400000m,200000m}) {
                Check(Quantity(available,feeRate:0)==20,
                    "twenty percent remains a fixed200000 target while100 through20 percent of the total budget remains available");
                Check(Quantity(available)==19,
                    "the same target and fee allowance produce the same nineteen-share order after earlier positions consume cash");
            }
            decimal balance=1000000m;
            for(int position=0;position<5;position++) {
                int quantity=Quantity(balance);
                Check(quantity==19,"successive positions keep the configured total-budget allocation instead of compounding a declining cash balance");
                balance-=quantity*10000m*1.003m;
            }
            Check(balance==47150m&&Quantity(balance)==4,
                "only actual remaining funds reduce the final order after five fixed-target positions");

            Check(Quantity(50000)==4,"a50000 current cap is spent up to its full amount rather than being multiplied by twenty percent again");
            Check(Quantity(50000,price:25000,feeRate:0)==2,
                "an affordable share is not rejected merely because twenty percent of the remaining cash would be below one share");
            Check(Quantity(50000,price:25000)==1,"fee allowance remains inside the limited current spend");
            Check(Quantity(400000,budget:2000000)==39,
                "the configured operating budget remains the allocation anchor independently of current cash");
            Check(Quantity(1000000,fraction:0.1m)==9&&Quantity(1000000,fraction:0.5m)==49,
                "the saved allocation fraction still changes the fixed target exactly once");
            Check(Quantity(1000000,fraction:1m)==99,"a full-budget allocation still reserves its configured fee allowance");
            Check(Quantity(200600,budget:1003000)==20,
                "the exact cost including fee is affordable at the spend boundary");
            Check(Quantity(200599.99m,budget:1003000)==19,
                "one cent below the next share's cost rounds down instead of overspending");
            Check(Quantity(10029.99m)==0&&Quantity(10030m)==1,
                "a single share requires its full price plus configured fee allowance");
            Check(Quantity(11000m,feeRate:0.1m)==1,"the maximum supported fee rate is included in sizing");

            foreach(decimal value in new[]{0m,-1m}) {
                Check(Quantity(value)==0,"zero or negative current capacity cannot be spent");
                Check(Quantity(1000000,budget:value)==0,"zero or negative configured budget cannot create a target");
                Check(Quantity(1000000,price:value)==0,"invalid prices cannot create quantities");
            }
            foreach(decimal fraction in new[]{0m,-0.1m,1.01m})
                Check(Quantity(1000000,fraction:fraction)==0,"invalid allocation fractions never create an order");
            foreach(decimal fee in new[]{-0.001m,0.1001m})
                Check(Quantity(1000000,feeRate:fee)==0,"unsupported fee rates never create an order");

            Check(Quantity(1000000000m,price:1m,feeRate:0,budget:1000000000m)==100000,
                "the existing one-hundred-thousand-share ceiling is preserved");
            Check(Quantity(decimal.MaxValue,price:0.0000000000000000000000000001m,feeRate:0,budget:decimal.MaxValue)==100000,
                "the quantity ceiling is applied without division overflow for extreme supported decimals");
            Check(Quantity(decimal.MaxValue,price:decimal.MaxValue,feeRate:0.1m,budget:decimal.MaxValue)==0,
                "a unit cost outside decimal range cannot produce an affordable order");
            Check(Rules.OrderQuantity(100000,10000)==1&&Rules.OrderQuantity(100000,10000,0.2m,0)==2,
                "legacy overloads retain their original behavior when the supplied amount is both total budget and available funds");
            Check(Rules.OrderQuantity(50000,25000,0.2m,0)==0&&Quantity(50000,25000,0)==2,
                "the explicit total-budget overload corrects the old residual-cash allocation regression");

            foreach(decimal available in new[]{47150m,50000m,100030m,200000m,800000m}) {
                int quantity=Quantity(available);
                decimal allowed=Math.Min(200000m,available),cost=10000m*1.003m;
                Check(quantity*cost<=allowed&&(quantity+1)*cost>allowed,
                    "whole-share rounding uses the largest affordable quantity while respecting both fixed target and available cap");
            }
            return checks;
        }
    }
}
