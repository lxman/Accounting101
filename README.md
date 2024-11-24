# Accounting101

So it's been some time since I have revisited this project and I've found that DevExpress has broken DI. Therefore the front end has been removed. I will be focusing on development of the accounting engine based on first principles that I have learned over my years in accounting projects.

I have spent a number of years working on commercial accounting products and one of my main goals here is to avoid some poor decisions that were made in the design of those products.

Some brief notes of some principles that I have learned:

1. Without the proper concepts clearly set out before the first line of code is written, accounting is hard.

2. The farther you go down the path of #1 without a clear understanding of the concepts, the harder it gets.

3. Accounting is fractal. (Coastline measurement problem)

4. Beginning to record balances with every transaction is the beginning of the death of your accounting system.

5. P&L and Balance Sheets are the calculus of the accounting world. If your project cannot produce accurate and repeatable P&L and Balance Sheets, you are not doing accounting. All you are doing is adding and subtracting numbers. That is not accounting.

6. There MUST be one single source of truth. The purpose of accounting is to measure account balances over time (voltmeter vs. oscilloscope). There MUST be one place you can go to in order to repeatably ask the question "What is the balance of account X at time Y."
