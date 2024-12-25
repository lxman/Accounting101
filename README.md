# Accounting101

The project should be usable enough at this point to provide a basic idea of how the accounting engine will work. The UI is still very rough and there are many features that are not yet implemented. The accounting engine is still in its infancy, but it is starting to take shape.
The ability is there to create, edit and delete clients and transactions. There is a simple balance sheet and P&L. I would consider it late alpha or early beta at this point.

The front end has been redesigned to use "pure" WPF (mostly in the MVVM pattern). I have used the WPF Community Toolkit (messaging has been a savior for transitioning the marshalling/threading issues). As this is my first project with MVVM using vanilla WPF, I'm sure there are many things which could be improved, but for now the UI works to get a basic idea of how things can perform.

I will be focusing on development of the accounting engine based on first principles that I have learned over my years in accounting projects.

I have spent a number of years working on commercial accounting products and one of my main goals here is to avoid some poor decisions that were made in the design of those products.

Some brief notes of some principles that I have learned:

1. Without the proper concepts clearly set out before the first line of code is written, accounting is hard.

2. The farther you go down the path of #1 without a clear understanding of the concepts, the harder it gets.

3. Accounting is fractal. (Coastline measurement problem)

4. Beginning to record balances with every transaction is the beginning of the death of your accounting system.

5. P&L and Balance Sheets are the calculus of the accounting world. If your project cannot produce accurate and repeatable P&L and Balance Sheets, you are not doing accounting. All you are doing is adding and subtracting numbers. That is not accounting.

6. There MUST be one single source of truth. The purpose of accounting is to measure account balances over time (voltmeter vs. oscilloscope). There MUST be one place you can go to in order to repeatably ask the question "What is the balance of account X at time Y."
