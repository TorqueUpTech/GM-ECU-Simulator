using System;
using System.Collections.Generic;
using System.Globalization;
using Common.Signals;
using Core.Ecu;

namespace GmEcuSimulator.ViewModels;

// Editable view of one DmrSignalMapping row (Ford persona only). Mirrors the broadcast row VMs:
// setters push straight into the model and notify the parent so the live broadcast loop picks up
// the change. The mapping wires a DMR RAM address PCMTec binds via $A1 to an engine SignalId.
public sealed class DmrSignalMappingViewModel : NotifyPropertyChangedBase
{
    public DmrSignalMapping Model { get; }
    private readonly EcuViewModel parent;

    public DmrSignalMappingViewModel(DmrSignalMapping model, EcuViewModel parent)
    {
        Model = model;
        this.parent = parent;
    }

    /// <summary>The DMR RAM address as a hex string (e.g. "0x003F7FA0").</summary>
    public string AddressHex
    {
        get => $"0x{Model.Address:X8}";
        set
        {
            if (TryParseHex(value, out uint addr) && addr != Model.Address)
            {
                Model.Address = addr;
                OnPropertyChanged();
                parent.OnDmrMappingEdited();
            }
        }
    }

    public string Name
    {
        get => Model.Name;
        set
        {
            if ((value ?? "") != Model.Name)
            {
                Model.Name = value ?? "";
                OnPropertyChanged();
                parent.OnDmrMappingEdited();
            }
        }
    }

    /// <summary>The engine signals available to drive this slot (the full catalogue).</summary>
    public IReadOnlyList<SignalDef> SignalOptions => SignalCatalogue.All;

    public SignalDef SelectedSignal
    {
        get => SignalCatalogue.Get(Model.Signal);
        set
        {
            if (value is not null && value.Id != Model.Signal)
            {
                Model.Signal = value.Id;
                OnPropertyChanged();
                parent.OnDmrMappingEdited();
            }
        }
    }

    private static readonly DmrValueEncoding[] AllEncodings =
        (DmrValueEncoding[])Enum.GetValues(typeof(DmrValueEncoding));

    /// <summary>All wire encodings for the value bytes.</summary>
    public IReadOnlyList<DmrValueEncoding> EncodingOptions => AllEncodings;

    public DmrValueEncoding SelectedEncoding
    {
        get => Model.Encoding;
        set
        {
            if (value != Model.Encoding)
            {
                Model.Encoding = value;
                OnPropertyChanged();
                parent.OnDmrMappingEdited();
            }
        }
    }

    public double Scale
    {
        get => Model.Scale;
        set
        {
            if (value != Model.Scale)
            {
                Model.Scale = value;
                OnPropertyChanged();
                parent.OnDmrMappingEdited();
            }
        }
    }

    public double Offset
    {
        get => Model.Offset;
        set
        {
            if (value != Model.Offset)
            {
                Model.Offset = value;
                OnPropertyChanged();
                parent.OnDmrMappingEdited();
            }
        }
    }

    private static bool TryParseHex(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
