using System;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace DataAccess.WPF.Services;

public class ChangeEventArgs
{
    public Type ChangedType { get; set; }

    public ChangeType ChangeType { get; set; }
}