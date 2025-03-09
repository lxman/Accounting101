﻿using System;

namespace DataAccess.WPF.Models;

public class Setting
{
    public Guid Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}