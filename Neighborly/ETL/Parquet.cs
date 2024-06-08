﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Meta;
using Parquet.Rows;
using Parquet.Schema;
using Parquet.Meta;
using System.Security.Cryptography;

namespace Neighborly.ETL
{
    /// <summary>
    /// ETL operation for importing and exporting Parquet files.
    /// </summary>
    public class Parquet : IETL
    {
        public bool isDirectory { get; set; }
        public string fileExtension => ".parquet";
        public VectorDatabase vectorDatabase { get; set; }

        public Task ExportDataAsync(string path)
        {
            if (isDirectory)
            {
                var files = System.IO.Directory.GetFiles(path, "*" + fileExtension);
                foreach (var file in files)
                {
                    // Export the data
                }
            }
            throw new NotImplementedException();
        }

        /// <summary>
        /// Attempts to find columns with byte array data and imports it into the VectorDatabase.
        /// </summary>
        /// <param name="path">Folder path containing the parquet file(s)</param>
        /// <returns></returns>
        public async Task ImportDataAsync(string path)
        {
            string[] files;
            if (this.isDirectory)
                files = System.IO.Directory.GetFiles(path, "*" + fileExtension);
            else
                files = new string[] { path };

            foreach (var file in files)
            {
                // Load the Parquet file
                if (File.Exists(file))
                {
                    using (Stream fs = System.IO.File.OpenRead(file))
                    {
                        using (ParquetReader reader = await ParquetReader.CreateAsync(fs))
                        {
                            // Iterate through the row groups in the file
                            for (int i = 0; i < reader.RowGroupCount; i++)
                            {
                                using (ParquetRowGroupReader groupReader = reader.OpenRowGroupReader(i))
                                {
                                    // Iterate through the columns in the row group
                                    foreach (DataField field in reader.Schema.GetDataFields())
                                    {
                                        // Check if the column is a float array (i.e. a Vector)
                                        if (field.ClrType == typeof(float[]))
                                        {
                                            // Read float array
                                            var data = await groupReader.ReadColumnAsync(field);
                                            var numValues = data.NumValues;
                                            if (numValues > 0)
                                            {
                                                // Convert float array to Vector
                                                float[] d = data.Data as float[];
                                                if (d != null)
                                                {
                                                    // Convert float array to Vector
                                                    var vector = new Vector(d);
                                                    vectorDatabase.Vectors.Add(vector);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

    }
}
