using System.Windows;
using FieldProbe.App.Hmi;

namespace FieldProbe.App.Views;

/// <summary>Janela de detalhes de um elemento da IHM (valor, qualidade, variável e erro), como um faceplate de SCADA. Só leitura.</summary>
public partial class FaceplateWindow : Window
{
    public FaceplateWindow(HmiElementViewModel element)
    {
        InitializeComponent();
        DataContext = element;
        Title = $"Detalhes · {element.Label}";
    }
}
