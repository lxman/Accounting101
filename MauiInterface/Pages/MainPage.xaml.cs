using MauiInterface.Models;
using MauiInterface.PageModels;

namespace MauiInterface.Pages
{
    public partial class MainPage : ContentPage
    {
        public MainPage(MainPageModel model)
        {
            InitializeComponent();
            BindingContext = model;
        }
    }
}