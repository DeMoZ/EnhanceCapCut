//! Lenient field access over parsed JSON, the port of `Lenient.swift`.
//!
//! CapCut drafts change between app versions. Every field is read leniently: a missing key, a `null` or a value of
//! an unexpected type yields `None` instead of failing the whole draft. The conversions reproduce what Foundation's
//! `JSONDecoder` accepts for each Swift type, so both implementations read the same values:
//!
//! - `Double`: any JSON number;
//! - `Int` / `Int64`: a JSON number with an integral value in range (`1.0` and `1e2` are integers, `1.5` is not);
//! - `Bool`: only `true` / `false`;
//! - `String`: only a JSON string.

use serde_json::{Map, Value};

/// `Double` as `JSONDecoder` reads it: any number.
pub fn as_double(v: &Value) -> Option<f64> {
    v.as_f64()
}

/// `Int` / `Int64` as `JSONDecoder` reads it: a number with an integral value that fits.
pub fn as_int(v: &Value) -> Option<i64> {
    let Value::Number(n) = v else { return None };
    if let Some(i) = n.as_i64() {
        return Some(i);
    }
    if n.is_u64() {
        return None; // above Int64.max
    }
    let d = n.as_f64()?;
    // -2^63 ..< 2^63, exactly representable bounds.
    if d.fract() == 0.0 && (-9_223_372_036_854_775_808.0..9_223_372_036_854_775_808.0).contains(&d) {
        Some(d as i64)
    } else {
        None
    }
}

/// Swift's `Double(String)`: the whole string must be a number (no surrounding spaces); accepts a sign, `inf`,
/// `infinity`, `nan` in any case, `.5`, `5.`, exponents and hexadecimal floats (`0x1p3`).
pub fn parse_swift_double(s: &str) -> Option<f64> {
    let (sign, body) = match s.as_bytes().first() {
        Some(b'-') => (-1.0, &s[1..]),
        Some(b'+') => (1.0, &s[1..]),
        _ => (1.0, s),
    };
    if body.len() > 2 && (body.starts_with("0x") || body.starts_with("0X")) {
        return parse_hex_float(&body[2..]).map(|v| sign * v);
    }
    // Rust's parser takes the same decimal forms; reject what it accepts but Swift doesn't.
    if body.is_empty() || body.starts_with(['+', '-']) {
        return None;
    }
    body.parse::<f64>().ok().map(|v| sign * v)
}

fn parse_hex_float(s: &str) -> Option<f64> {
    let (mantissa, exponent) = match s.find(['p', 'P']) {
        Some(i) => (&s[..i], s[i + 1..].parse::<i32>().ok()?),
        None => (s, 0),
    };
    let (int_part, frac_part) = mantissa.split_once('.').unwrap_or((mantissa, ""));
    if int_part.is_empty() && frac_part.is_empty() {
        return None;
    }
    let mut value = 0.0f64;
    for c in int_part.chars() {
        value = value * 16.0 + c.to_digit(16)? as f64;
    }
    let mut scale = 1.0 / 16.0;
    for c in frac_part.chars() {
        value += c.to_digit(16)? as f64 * scale;
        scale /= 16.0;
    }
    Some(value * 2f64.powi(exponent))
}

/// Keyed, lenient reads from a JSON object. On anything that is not an object every read is `None`.
pub trait Lenient {
    /// The value of `key`, unless it is missing or `null`.
    fn opt(&self, key: &str) -> Option<&Value>;

    /// The value of `key` when it is an object (Swift's `nestedContainer(keyedBy:forKey:)`).
    fn obj(&self, key: &str) -> Option<&Value> {
        self.opt(key).filter(|v| v.is_object())
    }

    /// A number, or a string holding one (`Lenient.swift` `num`).
    fn num(&self, key: &str) -> Option<f64> {
        match self.opt(key)? {
            Value::String(s) => parse_swift_double(s),
            v => as_double(v),
        }
    }

    /// `Int(c.num(key) ?? default)`: truncated toward zero.
    fn num_int(&self, key: &str, default: f64) -> i64 {
        self.num(key).unwrap_or(default) as i64
    }

    /// An integer; non-integral numbers are rounded half away from zero (`Int64(d.rounded())`).
    fn int64(&self, key: &str) -> Option<i64> {
        let v = self.opt(key)?;
        as_int(v).or_else(|| as_double(v).map(|d| d.round() as i64))
    }

    fn str(&self, key: &str) -> Option<&str> {
        self.opt(key)?.as_str()
    }

    /// `str(key) ?? ""` as an owned string.
    fn string(&self, key: &str) -> String {
        self.str(key).unwrap_or_default().to_string()
    }

    /// A bool, or an integer where non-zero is true.
    fn bool(&self, key: &str) -> Option<bool> {
        let v = self.opt(key)?;
        v.as_bool().or_else(|| as_int(v).map(|i| i != 0))
    }

    /// The elements of an array that `parse` accepts; bad elements are skipped (Swift's `Lossy`).
    fn list<T>(&self, key: &str, parse: impl Fn(&Value) -> Option<T>) -> Vec<T> {
        match self.opt(key) {
            Some(Value::Array(items)) => items.iter().filter_map(parse).collect(),
            _ => Vec::new(),
        }
    }

    /// `opt(key) as [Double]?`: all elements must be numbers, or the whole array is `None`.
    fn f64_array(&self, key: &str) -> Option<Vec<f64>> {
        self.opt(key)?.as_array()?.iter().map(as_double).collect()
    }

    /// `opt(key) as [Int]?`: all elements must be integers, or the whole array is `None`.
    fn int_array(&self, key: &str) -> Option<Vec<i64>> {
        self.opt(key)?.as_array()?.iter().map(as_int).collect()
    }

    /// `opt(key) as [String]?`: all elements must be strings, or the whole array is `None`.
    fn string_array(&self, key: &str) -> Option<Vec<String>> {
        self.opt(key)?.as_array()?.iter().map(|v| v.as_str().map(str::to_string)).collect()
    }

    /// The object's keys (`allKeys`), sorted; empty for a non-object.
    fn keys(&self) -> Vec<&str>;
}

impl Lenient for Value {
    fn opt(&self, key: &str) -> Option<&Value> {
        self.as_object().and_then(|m| m.opt(key))
    }

    fn keys(&self) -> Vec<&str> {
        self.as_object().map(Lenient::keys).unwrap_or_default()
    }
}

impl Lenient for Map<String, Value> {
    fn opt(&self, key: &str) -> Option<&Value> {
        match self.get(key)? {
            Value::Null => None,
            v => Some(v),
        }
    }

    fn keys(&self) -> Vec<&str> {
        Map::keys(self).map(String::as_str).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn numbers_accept_numeric_strings() {
        let v = json!({"a": "1.5", "b": 2, "c": "+2", "d": " 1", "e": "x", "f": "0x1p3", "g": ".5", "h": "inf"});
        assert_eq!(v.num("a"), Some(1.5));
        assert_eq!(v.num("b"), Some(2.0));
        assert_eq!(v.num("c"), Some(2.0));
        assert_eq!(v.num("d"), None);
        assert_eq!(v.num("e"), None);
        assert_eq!(v.num("f"), Some(8.0));
        assert_eq!(v.num("g"), Some(0.5));
        assert_eq!(v.num("h"), Some(f64::INFINITY));
        assert_eq!(parse_swift_double("+-1"), None);
        assert_eq!(parse_swift_double(""), None);
        assert_eq!(parse_swift_double("1_0"), None);
    }

    #[test]
    fn integers_round_doubles_and_reject_strings() {
        let v = json!({"a": 2.5, "b": -2.5, "c": 1e2, "d": "3", "e": 7, "f": 1.0});
        assert_eq!(v.int64("a"), Some(3));
        assert_eq!(v.int64("b"), Some(-3));
        assert_eq!(v.int64("c"), Some(100));
        assert_eq!(v.int64("d"), None);
        assert_eq!(v.int64("e"), Some(7));
        assert_eq!(v.int64("f"), Some(1));
        assert_eq!(v.num_int("a", 0.0), 2);
        assert_eq!(v.num_int("x", 1920.0), 1920);
    }

    #[test]
    fn booleans_accept_integers() {
        let v = json!({"a": true, "b": 1, "c": 0, "d": 1.5, "e": "true", "f": 2.0});
        assert_eq!(v.bool("a"), Some(true));
        assert_eq!(v.bool("b"), Some(true));
        assert_eq!(v.bool("c"), Some(false));
        assert_eq!(v.bool("d"), None);
        assert_eq!(v.bool("e"), None);
        assert_eq!(v.bool("f"), Some(true));
    }

    #[test]
    fn null_and_wrong_types_are_missing() {
        let v = json!({"a": null, "b": 5, "c": {"x": 1}});
        assert_eq!(v.num("a"), None);
        assert_eq!(v.str("b"), None);
        assert!(v.obj("b").is_none());
        assert!(v.obj("c").is_some());
        assert_eq!(json!([1]).num("a"), None);
        assert_eq!(v.string("a"), "");
    }

    #[test]
    fn lists_are_lossy_and_typed_arrays_strict() {
        let v =
            json!({"l": [1, "x", 3, null], "n": [1, 2.5], "m": [1, "2"], "i": [1, 2.0], "j": [1, 2.5], "s": ["a", 1]});
        assert_eq!(v.list("l", as_int), vec![1, 3]);
        assert!(v.list("n", as_int).len() == 1);
        assert_eq!(v.f64_array("n"), Some(vec![1.0, 2.5]));
        assert_eq!(v.f64_array("m"), None);
        assert_eq!(v.int_array("i"), Some(vec![1, 2]));
        assert_eq!(v.int_array("j"), None);
        assert_eq!(v.string_array("s"), None);
        assert!(v.list("missing", as_int).is_empty());
    }
}
