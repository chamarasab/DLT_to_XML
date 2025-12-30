using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Globalization;

public static class DltConverter
{
    public static bool Convert(string inputPath, string outputPath, string xsdPath = null)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Input DLT not found", inputPath);
        var lines = File.ReadAllLines(inputPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        // Load postal data if available
        var postalPath = Path.Combine("sources", "postal.sql");
        var postalByCode = new System.Collections.Generic.Dictionary<string, (string City, string District, string Province)>(StringComparer.OrdinalIgnoreCase);
        var postalByCity = new System.Collections.Generic.Dictionary<string, (string Code, string District, string Province)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(postalPath))
        {
            var sql = File.ReadAllText(postalPath);
            var rx = new System.Text.RegularExpressions.Regex("\\('(?<code>\\d{5})',\\s*'(?<city>[^']*)',\\s*'(?<district>[^']*)',\\s*'(?<province>[^']*)'\\)", System.Text.RegularExpressions.RegexOptions.Compiled);
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(sql))
            {
                var code = m.Groups["code"].Value;
                var city = m.Groups["city"].Value.Trim();
                var district = m.Groups["district"].Value.Trim();
                var province = m.Groups["province"].Value.Trim();
                if (!postalByCode.ContainsKey(code)) postalByCode[code] = (city, district, province);
                if (!string.IsNullOrEmpty(city) && !postalByCity.ContainsKey(city)) postalByCity[city] = (code, district, province);
            }
        }
        XNamespace ns = "http://creditinfo.com/schemas/CB5/SriLanka/bouncedcheque";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"));
        var batch = new XElement(ns + "Batch",
            new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
            new XAttribute(xsi + "schemaLocation", ns.NamespaceName)
        );

        var hdhd = lines.Select(l => l.Split('|')).FirstOrDefault(p => p.Length > 1 && p[0] == "HDHD");
        var batchId = hdhd != null && hdhd.Length > 1 ? hdhd[1] : "DLT_BATCH";
        batch.Add(new XElement(ns + "BatchIdentifier", batchId));

        foreach (var parts in lines.Select(l => l.Split('|')).Where(p => p.Length > 0 && p[0] == "CMDC"))
        {
            string Get(int i) => (parts.Length > i && !string.IsNullOrWhiteSpace(parts[i])) ? parts[i].Trim() : null;

            var branchId = Get(2) ?? "";
            var account = Get(3) ?? "";
            var chequeNumber = Get(4) ?? "";
            var amount = Get(5) ?? "";
            var currency = Get(6) ?? "";
            var dateStr = Get(7);
            DateTime? dishonoured = null;
            if (DateTime.TryParseExact(dateStr, new[] { "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MM-yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                dishonoured = dt;

            var reasonCode = Get(8);
            var reason = reasonCode == "001" ? "InsufficientFunds" : "Unknown";

            var customerCode = Get(10) ?? (account != null ? account + "-1" : null);
            var fullName = Get(17) ?? Get(18) ?? "";

            string addressLine = Get(19) ?? Get(28) ?? "";
            string postal = null;
            for (int i = 0; i < parts.Length; i++)
            {
                var t = Get(i);
                if (!string.IsNullOrEmpty(t) && t.All(c => char.IsDigit(c)) && t.Length >= 3 && t.Length <= 6)
                {
                    postal = t; break;
                }
            }

            var bounced = new XElement(ns + "BouncedCheque",
                new XElement(ns + "EntityCode", string.Join("-", new[] { account, chequeNumber, branchId }.Where(x => !string.IsNullOrEmpty(x))))
            );

            // format amount as decimal with two fraction digits
            string formattedAmount = amount;
            if (!string.IsNullOrEmpty(amount) && Decimal.TryParse(amount, NumberStyles.Any, CultureInfo.InvariantCulture, out var amtDec))
            {
                formattedAmount = amtDec.ToString("F2", CultureInfo.InvariantCulture);
            }

            var data = new XElement(ns + "BouncedChequeData",
                new XElement(ns + "BranchID", branchId),
                new XElement(ns + "ChequeNumber", chequeNumber),
                new XElement(ns + "ChequeAmount",
                    new XElement(ns + "Value", formattedAmount),
                    new XElement(ns + "Currency", currency)
                ),
                new XElement(ns + "AccountNumber", account)
            );

            if (dishonoured.HasValue)
                data.Add(new XElement(ns + "DateDishonoured", dishonoured.Value.ToString("yyyy-MM-dd")));
            else if (!string.IsNullOrEmpty(dateStr))
                data.Add(new XElement(ns + "DateDishonoured", dateStr));

            data.Add(new XElement(ns + "ReasonForDishonour", reason));
            bounced.Add(data);

            // Determine if this is a company or individual.
            bool LooksLikeCompany()
            {
                if (!string.IsNullOrEmpty(Get(10)))
                {
                    var code = Get(10);
                    // company codes often start with letters like PV, WA, K, etc.
                    if (code.Length >= 2 && char.IsLetter(code[0]) && code.Any(char.IsLetter)) return true;
                }
                if (!string.IsNullOrEmpty(fullName))
                {
                    var up = fullName.ToUpperInvariant();
                    if (up.Contains("PVT") || up.Contains("LTD") || up.Contains("COMPANY") || up.Contains("CO") || up.Contains("PLC") || up.Contains("PVT LTD")) return true;
                }
                return false;
            }

            if (LooksLikeCompany())
            {
                var company = new XElement(ns + "Company");
                var compCode = customerCode;
                if (!string.IsNullOrEmpty(compCode) && !compCode.Contains("-")) compCode = compCode + "-3";
                company.Add(new XElement(ns + "CustomerCode", compCode ?? string.Empty));
                company.Add(new XElement(ns + "CompanyName", fullName));
                company.Add(new XElement(ns + "LegalConstitution", "Other"));
                company.Add(new XElement(ns + "EconomicActivityType1", Get(13) ?? string.Empty));
                company.Add(new XElement(ns + "EconomicActivityType2", string.Empty));
                company.Add(new XElement(ns + "EconomicActivityType3", string.Empty));

                var idNumsC = new XElement(ns + "IdentificationNumbers",
                    new XElement(ns + "BusinessRegistrationNumber", Get(10) ?? string.Empty),
                    new XElement(ns + "BusinessRegistrationDate", (DateTime.TryParse(Get(14), out var bdt) ? bdt.ToString("yyyy-MM-dd") : string.Empty)),
                    new XElement(ns + "VATRegistrationNumber", string.Empty)
                );
                company.Add(idNumsC);

                // Fill mailing address fields using postal data when possible
                string mailCity = string.Empty, mailPostal = string.Empty, mailProv = string.Empty, mailDist = string.Empty;
                var cleanedAddress = addressLine ?? string.Empty;
                // try postal code in addressLine
                var pcRx = new System.Text.RegularExpressions.Regex("\\b(\\d{5})\\b");
                var pcMatch = pcRx.Match(cleanedAddress);
                if (pcMatch.Success)
                {
                    var code = pcMatch.Groups[1].Value;
                    if (postalByCode.TryGetValue(code, out var entry))
                    {
                        mailCity = entry.City;
                        mailPostal = code;
                        mailProv = entry.Province;
                        mailDist = entry.District;
                        cleanedAddress = cleanedAddress.Replace(code, "").Trim();
                    }
                }
                // if not found, try city name lookup in addressLine
                if (string.IsNullOrEmpty(mailCity) && !string.IsNullOrEmpty(cleanedAddress))
                {
                    foreach (var kv in postalByCity)
                    {
                        if (cleanedAddress.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            mailCity = kv.Key;
                            mailPostal = kv.Value.Code;
                            mailProv = kv.Value.Province;
                            mailDist = kv.Value.District;
                            // remove city mention from address
                            cleanedAddress = cleanedAddress.Replace(kv.Key, "", StringComparison.OrdinalIgnoreCase).Trim();
                            break;
                        }
                    }
                }
                // if still empty, try Get(26) as city
                if (string.IsNullOrEmpty(mailCity) && !string.IsNullOrEmpty(Get(26)))
                {
                    var cityCandidate = Get(26);
                    if (postalByCity.TryGetValue(cityCandidate, out var v))
                    {
                        mailCity = cityCandidate; mailPostal = v.Code; mailProv = v.Province; mailDist = v.District;
                    }
                }
                // fallback default
                if (string.IsNullOrEmpty(mailCity)) { mailCity = "Colombo 01"; mailPostal = "00100"; mailProv = "Western"; mailDist = "Colombo"; }

                var mailingC = new XElement(ns + "MailingAddress",
                    new XElement(ns + "City", mailCity),
                    new XElement(ns + "PostalCode", mailPostal),
                    new XElement(ns + "Province", mailProv),
                    new XElement(ns + "District", mailDist),
                    new XElement(ns + "Country", "LK"),
                    new XElement(ns + "AddressLine", cleanedAddress)
                );
                company.Add(mailingC);

                var permC = new XElement(ns + "PermanentAddress",
                    new XElement(ns + "City", mailCity),
                    new XElement(ns + "PostalCode", mailPostal),
                    new XElement(ns + "Province", mailProv),
                    new XElement(ns + "District", mailDist),
                    new XElement(ns + "Country", "LK"),
                    new XElement(ns + "AddressLine", cleanedAddress)
                );
                company.Add(permC);

                company.Add(new XElement(ns + "Contacts"));
                bounced.Add(company);

                // SubjectRole should use company code
                customerCode = compCode;
            }
            else
            {
                var individual = new XElement(ns + "Individual");
                if (!string.IsNullOrEmpty(customerCode) && !customerCode.Contains("-")) customerCode = customerCode + "-1";
                individual.Add(new XElement(ns + "CustomerCode", customerCode ?? string.Empty));
                individual.Add(new XElement(ns + "FullName", fullName));
                individual.Add(new XElement(ns + "Salutation", Get(15) ?? string.Empty));
                individual.Add(new XElement(ns + "Profession", Get(16) ?? string.Empty));
                individual.Add(new XElement(ns + "SpouseName", Get(17) ?? string.Empty));
                individual.Add(new XElement(ns + "ClassificationOfIndividual", "Individual"));
                individual.Add(new XElement(ns + "Gender", Get(20) ?? string.Empty));

                var dobStr2 = Get(21);
                individual.Add(new XElement(ns + "DateOfBirth", (DateTime.TryParse(dobStr2, out var dob2) ? dob2.ToString("yyyy-MM-dd") : string.Empty)));

                var ms2 = Get(22);
                var allowedMarital = new[] { "Unmarried", "Married", "Widowed", "Divorced", "Separated", "Single" };
                individual.Add(new XElement(ns + "MaritalStatus", (ms2 != null && ms2.Any(char.IsLetter) && allowedMarital.Any(a => string.Equals(a, ms2, StringComparison.OrdinalIgnoreCase)) ? ms2 : string.Empty)));
                individual.Add(new XElement(ns + "FateStatus", Get(23) ?? string.Empty));
                individual.Add(new XElement(ns + "Employment", Get(24) ?? string.Empty));
                individual.Add(new XElement(ns + "Residency", "Yes"));
                individual.Add(new XElement(ns + "EmployerName", string.Empty));
                individual.Add(new XElement(ns + "BusinessName", string.Empty));

                var idNums = new XElement(ns + "IdentificationNumbers",
                    new XElement(ns + "NICNumber", Get(10) ?? string.Empty),
                    new XElement(ns + "PassportNumber", Get(11) ?? string.Empty),
                    new XElement(ns + "DrivingLicenseNumber", Get(12) ?? string.Empty),
                    new XElement(ns + "BusinessRegistrationNumber", Get(13) ?? string.Empty),
                    new XElement(ns + "BusinessRegistrationDate", (DateTime.TryParse(Get(14), out var brdDt3) ? brdDt3.ToString("yyyy-MM-dd") : string.Empty))
                );
                individual.Add(idNums);

                var mailing = new XElement(ns + "MailingAddress",
                    new XElement(ns + "City", Get(26) ?? string.Empty),
                    new XElement(ns + "PostalCode", postal ?? string.Empty),
                    new XElement(ns + "Province", Get(27) ?? string.Empty),
                    new XElement(ns + "District", Get(28) ?? string.Empty),
                    new XElement(ns + "Country", "LK"),
                    new XElement(ns + "AddressLine", addressLine)
                );
                individual.Add(mailing);

                var permanent = new XElement(ns + "PermanentAddress",
                    new XElement(ns + "City", Get(26) ?? string.Empty),
                    new XElement(ns + "PostalCode", postal ?? string.Empty),
                    new XElement(ns + "Province", Get(27) ?? string.Empty),
                    new XElement(ns + "District", Get(28) ?? string.Empty),
                    new XElement(ns + "Country", "LK"),
                    new XElement(ns + "AddressLine", addressLine)
                );
                individual.Add(permanent);

                var contacts = new XElement(ns + "Contacts",
                    new XElement(ns + "MobilePhone", string.Empty),
                    new XElement(ns + "PhoneNumber", Get(29) ?? string.Empty),
                    new XElement(ns + "PhoneNumber2", Get(30) ?? string.Empty)
                );
                individual.Add(contacts);

                bounced.Add(individual);
            }

            var role = new XElement(ns + "SubjectRole",
                new XElement(ns + "CustomerCode", customerCode ?? string.Empty),
                new XElement(ns + "RoleOfCustomer", "Issuer")
            );
            bounced.Add(role);

            batch.Add(bounced);
        }

        doc.Add(batch);
        doc.Save(outputPath);

        // If XSD provided, validate and return whether validation succeeded
        if (!string.IsNullOrEmpty(xsdPath) && File.Exists(xsdPath))
        {
            var schemas = new XmlSchemaSet();
            schemas.Add(ns.NamespaceName, xsdPath);
            var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
            var hadErrors = false;
            settings.ValidationEventHandler += (s, e) => { hadErrors = true; };
            using (var xr = XmlReader.Create(outputPath, settings))
            {
                while (xr.Read()) { }
            }
            return !hadErrors;
        }

        return true;
    }
}
