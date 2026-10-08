using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace MyDynamicIsland
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            SuspendLayout();
            // 
            // Form1
            // 
            // Every element is custom-drawn from the pixel measurements in
            // Form1.cs. Letting WinForms auto-scale only the window produces
            // the oversized shell and clipped expanded content seen at non-100%
            // display scaling, so the form must keep those measurements exact.
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(176, 38);
            Name = "Form1";
            Text = "My Dynamic Island";
            Load += Form1_Load;
            ResumeLayout(true);
        }

        #endregion
    }
}
