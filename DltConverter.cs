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
                // Pick EconomicActivityType1 from likely DLT positions (13..16) — prefer values matching pattern like 09:01:001
                var econ1 = string.Empty;
                try
                {
                    var candidates = new[] { Get(13), Get(14), Get(15), Get(16) };
                    var rxCode = new System.Text.RegularExpressions.Regex("^\\d{2}:\\d{2}:\\d{3}$");
                    econ1 = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && rxCode.IsMatch(c)) ?? string.Empty;
                    // if we didn't find the colon-separated code, fallback to any non-empty that's not the placeholder '999'
                    if (string.IsNullOrEmpty(econ1)) econ1 = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && c != "999") ?? string.Empty;
                }
                catch { econ1 = Get(13) ?? Get(14) ?? string.Empty; }
                // Use EconomicActivityType1 from DLT when present and valid (exclude placeholder '999')
                if (!string.IsNullOrEmpty(econ1) && econ1 != "999") company.Add(new XElement(ns + "EconomicActivityType1", econ1));

                var idNumsC = new XElement(ns + "IdentificationNumbers",
                    new XElement(ns + "BusinessRegistrationNumber", Get(10) ?? string.Empty)
                );
                if (DateTime.TryParse(Get(14), out var bdtVal))
                    idNumsC.Add(new XElement(ns + "BusinessRegistrationDate", bdtVal.ToString("yyyy-MM-dd")));
                idNumsC.Add(new XElement(ns + "VATRegistrationNumber", string.Empty));
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
                        // Prefer postal-by-code entries as authoritative. If city contains qualifiers, log but still use the DB values.
                        try
                        {
                            var entryCityUpper = (entry.City ?? string.Empty).ToUpperInvariant();
                            var disallowedLog = new[] { "NEW TOWN", "NEWTOWN", "ESTATE", "GARDEN", "COLONY", "WATTA", "WATHTHA", "VILLAGE" };
                            if (disallowedLog.Any(d => entryCityUpper.Contains(d)))
                            {
                                var rejLog = Path.Combine("sources", "address_lookup_log.txt");
                                var rline = $"{DateTime.UtcNow:o}\tUsedPostalCodeWithQualifier:{code}\tCity:{entry.City}\tAddress:{cleanedAddress}";
                                File.AppendAllLines(rejLog, new[] { rline });
                            }
                        }
                        catch { }

                        mailCity = entry.City;
                        mailPostal = code;
                        mailProv = entry.Province;
                        mailDist = entry.District;
                        cleanedAddress = cleanedAddress.Replace(code, "").Trim();
                    }
                    else
                    {
                        // postal code present but not found in postal.sql — try the word before the code as a city candidate
                        try
                        {
                            var beforeRx = new System.Text.RegularExpressions.Regex($"\\b(\\w+)\\s+{code}\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            var beforeM = beforeRx.Match(cleanedAddress);
                            if (beforeM.Success)
                            {
                                var cand = beforeM.Groups[1].Value.Trim();
                                if (!string.IsNullOrEmpty(cand) && postalByCity.TryGetValue(cand, out var byCityEntry))
                                {
                                    var candUpper = cand.ToUpperInvariant();
                                    var disallowed = new[] { "NEW TOWN", "NEWTOWN", "ESTATE", "GARDEN", "COLONY", "WATTA", "WATHTHA", "VILLAGE" };
                                    if (!disallowed.Any(d => candUpper.Contains(d)))
                                    {
                                        mailCity = cand;
                                        mailPostal = byCityEntry.Code;
                                        mailProv = byCityEntry.Province;
                                        mailDist = byCityEntry.District;
                                        cleanedAddress = cleanedAddress.Replace(code, "").Trim();
                                        cleanedAddress = System.Text.RegularExpressions.Regex.Replace(cleanedAddress, "\\b" + System.Text.RegularExpressions.Regex.Escape(cand) + "\\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                                    }
                                    else
                                    {
                                        try { File.AppendAllLines(Path.Combine("sources", "address_lookup_log.txt"), new[] { $"{DateTime.UtcNow:o}\tRejectedCandidateCity:{cand}\tForPostal:{code}\tAddress:{cleanedAddress}" }); } catch { }
                                    }
                                }
                                else
                                {
                                    try { File.AppendAllLines(Path.Combine("sources", "address_lookup_log.txt"), new[] { $"{DateTime.UtcNow:o}\tPostalCodeNotInDb:{code}\tTriedCandidate:{cand}\tAddress:{cleanedAddress}" }); } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
                // if not found, try city name lookup in addressLine (require whole-word match)
                if (string.IsNullOrEmpty(mailCity) && !string.IsNullOrEmpty(cleanedAddress))
                {
                    foreach (var kv in postalByCity)
                    {
                        try
                        {
                            var pattern = "\\b" + System.Text.RegularExpressions.Regex.Escape(kv.Key) + "\\b";
                            if (System.Text.RegularExpressions.Regex.IsMatch(cleanedAddress, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            {
                                // reject matches that include disallowed qualifiers (treat as unknown)
                                var keyUpper = kv.Key.ToUpperInvariant();
                                var disallowed = new[] { "NEW TOWN", "NEWTOWN", "ESTATE", "GARDEN", "COLONY", "WATTA", "WATHTHA", "VILLAGE" };
                                if (disallowed.Any(d => keyUpper.Contains(d)))
                                {
                                    // log rejected match
                                    try
                                    {
                                        var rejLog = Path.Combine("sources", "address_lookup_log.txt");
                                        var rline = $"{DateTime.UtcNow:o}\tRejectedMatch:{kv.Key}\tAddress:{cleanedAddress}";
                                        File.AppendAllLines(rejLog, new[] { rline });
                                    }
                                    catch { }
                                    continue;
                                }

                                mailCity = kv.Key;
                                mailPostal = kv.Value.Code;
                                mailProv = kv.Value.Province;
                                mailDist = kv.Value.District;
                                // remove matched city mention from address
                                cleanedAddress = System.Text.RegularExpressions.Regex.Replace(cleanedAddress, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                                break;
                            }
                        }
                        catch
                        {
                            // ignore regex failures for particular keys
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
                var usedDefault = false;
                if (string.IsNullOrEmpty(mailCity)) { mailCity = "Colombo 01"; mailPostal = "00100"; mailProv = "Western"; mailDist = "Colombo"; usedDefault = true; }
                // sanitize province/district values (avoid full address strings ending up here)
                if (!string.IsNullOrEmpty(mailProv) && (mailProv.Any(char.IsDigit) || mailProv.Length > 40)) mailProv = string.Empty;
                if (!string.IsNullOrEmpty(mailDist) && (mailDist.Any(char.IsDigit) || mailDist.Length > 60)) mailDist = string.Empty;

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

                // log address lookup results (helpful to audit fallbacks)
                try
                {
                    var logPath = Path.Combine("sources", "address_lookup_log.txt");
                    var entity = compCode ?? fullName ?? account ?? "unknown";
                    var logLine = $"{DateTime.UtcNow:o}\tEntity:{entity}\tUsedDefault:{usedDefault}\tCity:{mailCity}\tPostal:{mailPostal}\tCleanedAddress:{cleanedAddress}";
                    File.AppendAllLines(logPath, new[] { logLine });
                }
                catch
                {
                    // ignore logging failures
                }

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
                if (DateTime.TryParse(dobStr2, out var dob2))
                    individual.Add(new XElement(ns + "DateOfBirth", dob2.ToString("yyyy-MM-dd")));

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
                    new XElement(ns + "BusinessRegistrationNumber", Get(13) ?? string.Empty)
                );
                if (DateTime.TryParse(Get(14), out var brdDt3)) idNums.Add(new XElement(ns + "BusinessRegistrationDate", brdDt3.ToString("yyyy-MM-dd")));
                individual.Add(idNums);

                // sanitize province/district for individuals
                var indProvince = Get(27) ?? string.Empty;
                var indDistrict = Get(28) ?? string.Empty;
                if (indProvince.Any(char.IsDigit) || indProvince.Length > 40) indProvince = string.Empty;
                if (indDistrict.Any(char.IsDigit) || indDistrict.Length > 60) indDistrict = string.Empty;

                // Enrich individual mailing/permanent addresses using postal.sql similar to company handling
                string indMailCity = string.Empty, indMailPostal = string.Empty, indMailProv = string.Empty, indMailDist = string.Empty;
                var indCleanedAddress = addressLine ?? string.Empty;
                var disallowed = new[] { "NEW TOWN", "NEWTOWN", "ESTATE", "GARDEN", "COLONY", "WATTA", "WATHTHA", "VILLAGE" };

                // 1) If a postal token was found in the row, prefer it
                if (!string.IsNullOrEmpty(postal))
                {
                    if (postalByCode.TryGetValue(postal, out var pentry))
                    {
                        // Accept postal-by-code as authoritative; log if city contains qualifier words but still use values.
                        try
                        {
                            var entryCityUpper = (pentry.City ?? string.Empty).ToUpperInvariant();
                            var disallowedLog = new[] { "NEW TOWN", "NEWTOWN", "ESTATE", "GARDEN", "COLONY", "WATTA", "WATHTHA", "VILLAGE" };
                            if (disallowedLog.Any(d => entryCityUpper.Contains(d)))
                            {
                                File.AppendAllLines(Path.Combine("sources", "address_lookup_log.txt"), new[] { $"{DateTime.UtcNow:o}\tUsedPostalCodeWithQualifier:{postal}\tCity:{pentry.City}\tAddress:{indCleanedAddress}" });
                            }
                        }
                        catch { }

                        indMailCity = pentry.City; indMailPostal = postal; indMailProv = pentry.Province; indMailDist = pentry.District;
                        indCleanedAddress = indCleanedAddress.Replace(postal, "").Trim();
                    }
                    else
                    {
                        // try the word before the postal code as a city candidate
                        try
                        {
                            var beforeRx = new System.Text.RegularExpressions.Regex($"\\b(\\w+)\\s+{postal}\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            var beforeM = beforeRx.Match(indCleanedAddress);
                            if (beforeM.Success)
                            {
                                var cand = beforeM.Groups[1].Value.Trim();
                                if (!string.IsNullOrEmpty(cand) && postalByCity.TryGetValue(cand, out var byCityEntry))
                                {
                                    var candUpper = cand.ToUpperInvariant();
                                    if (!disallowed.Any(d => candUpper.Contains(d)))
                                    {
                                        indMailCity = cand; indMailPostal = byCityEntry.Code; indMailProv = byCityEntry.Province; indMailDist = byCityEntry.District;
                                        indCleanedAddress = indCleanedAddress.Replace(postal, "").Trim();
                                        indCleanedAddress = System.Text.RegularExpressions.Regex.Replace(indCleanedAddress, "\\b" + System.Text.RegularExpressions.Regex.Escape(cand) + "\\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                                    }
                                    else
                                    {
                                        try { File.AppendAllLines(Path.Combine("sources", "address_lookup_log.txt"), new[] { $"{DateTime.UtcNow:o}\tRejectedCandidateCity:{cand}\tForPostal:{postal}\tAddress:{indCleanedAddress}" }); } catch { }
                                    }
                                }
                                else
                                {
                                    try { File.AppendAllLines(Path.Combine("sources", "address_lookup_log.txt"), new[] { $"{DateTime.UtcNow:o}\tPostalCodeNotInDb:{postal}\tTriedCandidate:{cand}\tAddress:{indCleanedAddress}" }); } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 2) If still not found, try whole-word city matches from postalByCity
                if (string.IsNullOrEmpty(indMailCity) && !string.IsNullOrEmpty(indCleanedAddress))
                {
                    foreach (var kv in postalByCity)
                    {
                        try
                        {
                            var pattern = "\\b" + System.Text.RegularExpressions.Regex.Escape(kv.Key) + "\\b";
                            if (System.Text.RegularExpressions.Regex.IsMatch(indCleanedAddress, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            {
                                var keyUpper = kv.Key.ToUpperInvariant();
                                if (disallowed.Any(d => keyUpper.Contains(d)))
                                {
                                    try { File.AppendAllLines(Path.Combine("sources", "address_lookup_log.txt"), new[] { $"{DateTime.UtcNow:o}\tRejectedMatch:{kv.Key}\tAddress:{indCleanedAddress}" }); } catch { }
                                    continue;
                                }

                                indMailCity = kv.Key; indMailPostal = kv.Value.Code; indMailProv = kv.Value.Province; indMailDist = kv.Value.District;
                                indCleanedAddress = System.Text.RegularExpressions.Regex.Replace(indCleanedAddress, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                                break;
                            }
                        }
                        catch { }
                    }
                }

                // 3) try Get(26) if present
                if (string.IsNullOrEmpty(indMailCity) && !string.IsNullOrEmpty(Get(26)))
                {
                    var cityCandidate = Get(26);
                    if (postalByCity.TryGetValue(cityCandidate, out var v))
                    {
                        indMailCity = cityCandidate; indMailPostal = v.Code; indMailProv = v.Province; indMailDist = v.District;
                    }
                }

                // 4) fallback default
                var indUsedDefault = false;
                if (string.IsNullOrEmpty(indMailCity)) { indMailCity = "Colombo 01"; indMailPostal = "00100"; indMailProv = "Western"; indMailDist = "Colombo"; indUsedDefault = true; }

                // sanitize province/district
                if (!string.IsNullOrEmpty(indMailProv) && (indMailProv.Any(char.IsDigit) || indMailProv.Length > 40)) indMailProv = string.Empty;
                if (!string.IsNullOrEmpty(indMailDist) && (indMailDist.Any(char.IsDigit) || indMailDist.Length > 60)) indMailDist = string.Empty;

                var mailing = new XElement(ns + "MailingAddress",
                    new XElement(ns + "City", indMailCity),
                    new XElement(ns + "PostalCode", indMailPostal),
                    new XElement(ns + "Province", indMailProv),
                    new XElement(ns + "District", indMailDist),
                    new XElement(ns + "Country", "LK"),
                    new XElement(ns + "AddressLine", indCleanedAddress)
                );
                individual.Add(mailing);

                var permanent = new XElement(ns + "PermanentAddress",
                    new XElement(ns + "City", indMailCity),
                    new XElement(ns + "PostalCode", indMailPostal),
                    new XElement(ns + "Province", indMailProv),
                    new XElement(ns + "District", indMailDist),
                    new XElement(ns + "Country", "LK"),
                    new XElement(ns + "AddressLine", indCleanedAddress)
                );
                individual.Add(permanent);

                // log individual address lookup
                try
                {
                    var logPath2 = Path.Combine("sources", "address_lookup_log.txt");
                    var ent2 = customerCode ?? fullName ?? account ?? "unknown";
                    var logLine2 = $"{DateTime.UtcNow:o}\tEntity:{ent2}\tUsedDefault:{indUsedDefault}\tCity:{indMailCity}\tPostal:{indMailPostal}\tCleanedAddress:{indCleanedAddress}";
                    File.AppendAllLines(logPath2, new[] { logLine2 });
                }
                catch { }

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
            var messages = new System.Collections.Generic.List<string>();
            settings.ValidationEventHandler += (s, e) =>
            {
                hadErrors = true;
                var ex = e.Exception as XmlSchemaException;
                var loc = ex != null ? $"(Line {ex.LineNumber}, Pos {ex.LinePosition})" : string.Empty;
                messages.Add($"{e.Severity}: {e.Message} {loc}");
            };
            using (var xr = XmlReader.Create(outputPath, settings))
            {
                while (xr.Read()) { }
            }

            if (hadErrors)
            {
                try
                {
                    var logPath = Path.Combine("sources", "validation_errors.txt");
                    File.WriteAllLines(logPath, messages);
                    Console.Error.WriteLine($"Validation failed with {messages.Count} issues. See {logPath}");
                    foreach (var m in messages) Console.Error.WriteLine(m);
                }
                catch
                {
                    // ignore logging failures
                }
            }

            return !hadErrors;
        }

        return true;
    }
}
