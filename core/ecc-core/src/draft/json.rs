//! Byte-preserving draft editing, the port of `DraftJSON.swift`: `JsonScanner` locates values inside a draft's JSON
//! text, `JsonEdits` replaces byte ranges, and `json_text` serializes new values the way CapCut (and the Swift
//! version) writes them. Everything we don't touch stays byte-for-byte as CapCut wrote it (key order, number
//! formatting, unknown fields).

use std::ops::Range;

use serde_json::Value;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ScanError {
    Malformed(usize),
}

impl std::fmt::Display for ScanError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ScanError::Malformed(offset) => {
                write!(f, "The draft is not valid JSON (at byte {offset}).")
            }
        }
    }
}

impl std::error::Error for ScanError {}

/// Locates values inside a draft's JSON text so the draft can be edited in place.
pub struct JsonScanner<'a> {
    pub bytes: &'a [u8],
}

fn is_space(b: u8) -> bool {
    matches!(b, b' ' | b'\t' | b'\n' | b'\r')
}

impl<'a> JsonScanner<'a> {
    pub fn new(bytes: &'a [u8]) -> JsonScanner<'a> {
        JsonScanner { bytes }
    }

    /// The whole document's value.
    pub fn root(&self) -> Result<Range<usize>, ScanError> {
        let start = self.skip_space(0);
        Ok(start..self.value_end(start)?)
    }

    /// Members of the object spanning `range`, in document order.
    pub fn members(&self, range: Range<usize>) -> Result<Vec<(String, Range<usize>)>, ScanError> {
        if self.bytes.get(range.start) != Some(&b'{') {
            return Err(ScanError::Malformed(range.start));
        }
        let mut out = Vec::new();
        let mut i = self.skip_space(range.start + 1);
        while i < range.end && self.bytes[i] != b'}' {
            let key_end = self.string_end(i)?;
            let key = serde_json::from_slice::<String>(&self.bytes[i..key_end]).unwrap_or_default();
            i = self.skip_space(key_end);
            if i >= self.bytes.len() || self.bytes[i] != b':' {
                return Err(ScanError::Malformed(i));
            }
            let start = self.skip_space(i + 1);
            let end = self.value_end(start)?;
            out.push((key, start..end));
            i = self.skip_space(end);
            if i < self.bytes.len() && self.bytes[i] == b',' {
                i = self.skip_space(i + 1);
            }
        }
        Ok(out)
    }

    /// The value of `key` in the object spanning `range`.
    pub fn value(&self, key: &str, range: Range<usize>) -> Result<Option<Range<usize>>, ScanError> {
        Ok(self.members(range)?.into_iter().find(|(k, _)| k == key).map(|(_, v)| v))
    }

    /// Elements of the array spanning `range`.
    pub fn elements(&self, range: Range<usize>) -> Result<Vec<Range<usize>>, ScanError> {
        if self.bytes.get(range.start) != Some(&b'[') {
            return Err(ScanError::Malformed(range.start));
        }
        let mut out = Vec::new();
        let mut i = self.skip_space(range.start + 1);
        while i < range.end && self.bytes[i] != b']' {
            let end = self.value_end(i)?;
            out.push(i..end);
            i = self.skip_space(end);
            if i < self.bytes.len() && self.bytes[i] == b',' {
                i = self.skip_space(i + 1);
            }
        }
        Ok(out)
    }

    /// Parses the value spanning `range` (for reading; edits go through `JsonEdits`).
    pub fn object(&self, range: Range<usize>) -> Option<Value> {
        serde_json::from_slice(&self.bytes[range]).ok()
    }

    fn skip_space(&self, start: usize) -> usize {
        let mut i = start;
        while i < self.bytes.len() && is_space(self.bytes[i]) {
            i += 1;
        }
        i
    }

    fn string_end(&self, start: usize) -> Result<usize, ScanError> {
        if self.bytes.get(start) != Some(&b'"') {
            return Err(ScanError::Malformed(start));
        }
        let mut i = start + 1;
        while i < self.bytes.len() {
            match self.bytes[i] {
                b'\\' => i += 2,
                b'"' => return Ok(i + 1),
                _ => i += 1,
            }
        }
        Err(ScanError::Malformed(start))
    }

    fn value_end(&self, start: usize) -> Result<usize, ScanError> {
        let Some(&first) = self.bytes.get(start) else {
            return Err(ScanError::Malformed(start));
        };
        match first {
            b'"' => self.string_end(start),
            b'{' | b'[' => {
                let mut depth = 0usize;
                let mut i = start;
                while i < self.bytes.len() {
                    match self.bytes[i] {
                        b'"' => {
                            i = self.string_end(i)?;
                            continue;
                        }
                        b'{' | b'[' => depth += 1,
                        b'}' | b']' => {
                            depth -= 1;
                            if depth == 0 {
                                return Ok(i + 1);
                            }
                        }
                        _ => {}
                    }
                    i += 1;
                }
                Err(ScanError::Malformed(start))
            }
            _ => {
                // Number, true, false or null.
                let mut i = start;
                while i < self.bytes.len()
                    && !matches!(self.bytes[i], b',' | b'}' | b']' | b' ' | b'\t' | b'\r' | b'\n')
                {
                    i += 1;
                }
                if i == start {
                    return Err(ScanError::Malformed(start));
                }
                Ok(i)
            }
        }
    }
}

/// Replacements of byte ranges, applied back to front.
#[derive(Debug, Clone, Default)]
pub struct JsonEdits {
    edits: Vec<(Range<usize>, Vec<u8>)>,
}

impl JsonEdits {
    pub fn new() -> JsonEdits {
        JsonEdits::default()
    }

    pub fn replace(&mut self, range: Range<usize>, text: &str) {
        self.edits.push((range, text.as_bytes().to_vec()));
    }

    pub fn is_empty(&self) -> bool {
        self.edits.is_empty()
    }

    pub fn applied(&self, bytes: &[u8]) -> Vec<u8> {
        let mut out = bytes.to_vec();
        let mut edits: Vec<_> = self.edits.iter().collect();
        // Stable sort by descending start, like Swift's `sorted(by: >)` on distinct starts.
        edits.sort_by_key(|e| std::cmp::Reverse(e.0.start));
        for (range, text) in edits {
            out.splice(range.clone(), text.iter().copied());
        }
        out
    }
}

/// Serializes a value like CapCut writes it: compact, slashes unescaped, object keys sorted, and numbers that were
/// floating point keep a decimal point (`1.0`, not `1`), formatted like Swift's `Double.description`.
pub fn json_text(value: &Value) -> String {
    let mut out = String::new();
    write_value(value, &mut out);
    out
}

fn write_value(value: &Value, out: &mut String) {
    match value {
        Value::Null => out.push_str("null"),
        Value::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
        Value::Number(n) => {
            if n.is_f64() {
                let d = n.as_f64().unwrap_or(0.0);
                out.push_str(&if d.is_finite() { swift_double(d) } else { "0.0".into() });
            } else if let Some(i) = n.as_i64() {
                out.push_str(&i.to_string());
            } else {
                out.push_str(&n.to_string());
            }
        }
        Value::String(s) => write_string(s, out),
        Value::Array(items) => {
            out.push('[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                write_value(item, out);
            }
            out.push(']');
        }
        Value::Object(map) => {
            out.push('{');
            let mut keys: Vec<&String> = map.keys().collect();
            keys.sort();
            for (i, key) in keys.into_iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                write_string(key, out);
                out.push(':');
                write_value(&map[key], out);
            }
            out.push('}');
        }
    }
}

fn write_string(s: &str, out: &mut String) {
    out.push('"');
    for ch in s.chars() {
        match ch {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
}

/// A JSON string literal for `s`, escaped like `json_text`.
pub fn json_string(s: &str) -> String {
    let mut out = String::new();
    write_string(s, &mut out);
    out
}

/// Swift's `Double.description` (`"\(d)"`): the shortest digits that round-trip, as `1.0`, `0.1`, `29.97`,
/// `1234567.125`, switching to exponent form below 1e-4 and above 2^53 (`1e-05`, `1e+16`,
/// `1.2345678901234568e+17`). NaN and infinities print as `nan`, `inf`, `-inf`.
pub fn swift_double(d: f64) -> String {
    if d.is_nan() {
        return "nan".into();
    }
    if d.is_infinite() {
        return if d > 0.0 { "inf".into() } else { "-inf".into() };
    }
    if d == 0.0 {
        return if d.is_sign_negative() { "-0.0".into() } else { "0.0".into() };
    }
    // Rust's `{:e}` gives the shortest round-trip digit count ("1.2345e-5", "1e16"), but breaks an exact tie
    // between two shortest candidates upwards; Swift takes the candidate closest to the exact value, ties to even,
    // which is the exactly rounded number with that many digits.
    let shortest = format!("{:e}", d.abs());
    let count = shortest.split_once('e').map_or(1, |(m, _)| m.chars().filter(char::is_ascii_digit).count());
    let sci = format!("{:.*e}", count - 1, d.abs());
    let (mantissa, exp) = sci.split_once('e').expect("exponent form");
    let exp: i32 = exp.parse().expect("exponent");
    let digits: String = mantissa.chars().filter(|c| *c != '.').collect();
    let sign = if d < 0.0 { "-" } else { "" };
    let magnitude = d.abs();
    if magnitude < 1e-4 || magnitude > 9_007_199_254_740_992.0 {
        let rest = if digits.len() > 1 { format!(".{}", &digits[1..]) } else { String::new() };
        let exp_sign = if exp < 0 { '-' } else { '+' };
        return format!("{sign}{}{rest}e{exp_sign}{:02}", &digits[..1], exp.abs());
    }
    let point = exp + 1; // digits before the decimal point
    let n = digits.len() as i32;
    let body = if point <= 0 {
        format!("0.{}{}", "0".repeat((-point) as usize), digits)
    } else if point >= n {
        format!("{}{}.0", digits, "0".repeat((point - n) as usize))
    } else {
        format!("{}.{}", &digits[..point as usize], &digits[point as usize..])
    };
    format!("{sign}{body}")
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn swift_double_matches_swift_description() {
        // Outputs of Swift's `"\(d)"` (Swift 6.4).
        let cases: [(f64, &str); 28] = [
            (0.0, "0.0"),
            (-0.0, "-0.0"),
            (1.0, "1.0"),
            (0.1, "0.1"),
            (1e-5, "1e-05"),
            (1e-4, "0.0001"),
            (0.00012345, "0.00012345"),
            (123456789.0, "123456789.0"),
            (1e15, "1000000000000000.0"),
            (1e16, "1e+16"),
            (9007199254740992.0, "9007199254740992.0"),
            (123456789012345680.0, "1.2345678901234568e+17"),
            (1.5e300, "1.5e+300"),
            (5e-324, "5e-324"),
            (0.30000000000000004, "0.30000000000000004"),
            (29.97, "29.97"),
            (30.0, "30.0"),
            (2.5e-7, "2.5e-07"),
            (1234567.125, "1234567.125"),
            (100.0, "100.0"),
            (1e21, "1e+21"),
            (1e22, "1e+22"),
            (-1e-7, "-1e-07"),
            (12345678901234567890.0, "1.2345678901234567e+19"),
            (999999999999999.9, "999999999999999.9"),
            (9999999999999998.0, "9.999999999999998e+15"),
            (f64::from_bits(4821807710155533700), "233115890514796.12"), // exactly …796.125
            (f64::from_bits(14059311489450460289), "-2020163013628192.2"), // exactly …192.25
        ];
        for (d, expected) in cases {
            assert_eq!(swift_double(d), expected, "{d:?}");
        }
    }

    #[test]
    fn json_text_writes_like_capcut() {
        let v: Value = serde_json::from_str(
            r#"{"b": 1.0, "a": [1, 0.1, 1e-05, true, null, "x/y\n\u0001é"], "c": {"z": 2, "y": 1e5}}"#,
        )
        .unwrap();
        assert_eq!(json_text(&v), r#"{"a":[1,0.1,1e-05,true,null,"x/y\n\u0001é"],"b":1.0,"c":{"y":100000.0,"z":2}}"#);
        assert_eq!(json_text(&json!(9_007_199_254_740_993i64)), "9007199254740993");
        assert_eq!(json_string("say \"hi\"\t\\"), r#""say \"hi\"\t\\""#);
    }

    #[test]
    fn scanner_finds_members_and_elements() {
        let text = br#" {"a": 1, "b" :[ 1, {"x":"}]"} , 3 ], "c\"d": "e", "n": null } "#;
        let s = JsonScanner::new(text);
        let root = s.root().unwrap();
        assert_eq!(&text[root.clone()][..1], b"{");
        let members = s.members(root.clone()).unwrap();
        let keys: Vec<_> = members.iter().map(|m| m.0.as_str()).collect();
        assert_eq!(keys, ["a", "b", "c\"d", "n"]);
        let b = s.value("b", root.clone()).unwrap().unwrap();
        let elements = s.elements(b).unwrap();
        assert_eq!(elements.len(), 3);
        assert_eq!(&text[elements[1].clone()], br#"{"x":"}]"}"#);
        assert_eq!(s.object(elements[1].clone()), Some(json!({"x": "}]"})));
        assert_eq!(&text[s.value("n", root.clone()).unwrap().unwrap()], b"null");
        assert_eq!(s.value("zz", root).unwrap(), None);
    }

    #[test]
    fn scanner_reports_malformed_offsets() {
        let s = JsonScanner::new(br#"{"a": "#);
        let err = s.root().unwrap_err();
        assert_eq!(err, ScanError::Malformed(0));
        let s = JsonScanner::new(br#"{"a" 1}"#);
        assert_eq!(s.members(0..7).unwrap_err(), ScanError::Malformed(5));
        assert_eq!(err.to_string(), "The draft is not valid JSON (at byte 0).");
    }

    #[test]
    fn edits_apply_back_to_front_and_keep_the_rest() {
        let text = br#"{"a": 1.50, "b": [ 1,2 ], "c": "keep"}"#;
        let s = JsonScanner::new(text);
        let root = s.root().unwrap();
        let mut edits = JsonEdits::new();
        edits.replace(s.value("b", root.clone()).unwrap().unwrap(), "[3]");
        edits.replace(s.value("a", root).unwrap().unwrap(), "2.0");
        assert_eq!(edits.applied(text), br#"{"a": 2.0, "b": [3], "c": "keep"}"#);
    }
}
