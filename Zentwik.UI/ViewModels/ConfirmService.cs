using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Zentwik.UI.ViewModels;

public sealed class ConfirmService : ViewModelBase
{
    public static ConfirmService Instance { get; } = new();

    private ConfirmService()
    {
        ConfirmCommand = new RelayCommand(_ => Confirm());
        CancelCommand = new RelayCommand(_ => Cancel());
    }

    private Action? _onConfirm;

    private bool _isOpen;
    private string _title = string.Empty;
    private string _confirmText = "确认";

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string ConfirmText
    {
        get => _confirmText;
        private set => SetProperty(ref _confirmText, value);
    }

    public ObservableCollection<string> Consequences { get; } = new();

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    public void Request(string title, IEnumerable<string> consequences, Action onConfirm, string confirmText = "确认")
    {
        _onConfirm = onConfirm;
        Title = title;
        ConfirmText = confirmText;

        Consequences.Clear();

        foreach (string consequence in consequences)
        {
            Consequences.Add(consequence);
        }

        IsOpen = true;
    }

    private void Confirm()
    {
        if (!IsOpen) return;

        // 先收起再执行：回调里若又发起了一次 Request，
        // 不会被这次 Close 清掉。
        Action? action = _onConfirm;
        Close();
        action?.Invoke();
    }

    private void Cancel() => Close();

    private void Close()
    {
        IsOpen = false;
        _onConfirm = null;
        Consequences.Clear();
    }
}
