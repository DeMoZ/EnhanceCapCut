//! Core → FFI conversion. `ecc-core` knows nothing of UniFFI, so every exported type is declared again here; the
//! `record!` and `simple_enum!` macros declare a type and its conversion from the core type in one place, one line
//! per field, so the two can't drift apart (design decision 4, "Mapping cost").

use std::collections::{BTreeMap, HashMap};
use std::hash::Hash;
use std::path::{Path, PathBuf};

/// A core value as the FFI sees it.
pub trait Ffi {
    type Out;
    fn ffi(self) -> Self::Out;
}

macro_rules! same {
    ($($t:ty),*) => {
        $(impl Ffi for $t {
            type Out = $t;
            fn ffi(self) -> $t {
                self
            }
        })*
    };
}
same!(i64, u64, f64, bool, String);

/// Indices (into `layers`, `filters`): `u64` on the other side.
impl Ffi for usize {
    type Out = u64;
    fn ffi(self) -> u64 {
        self as u64
    }
}

/// Paths are strings across the boundary (Swift extension `.url`, C# as is).
impl Ffi for PathBuf {
    type Out = String;
    fn ffi(self) -> String {
        path_string(&self)
    }
}

pub fn path_string(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

impl<T: Ffi> Ffi for Vec<T> {
    type Out = Vec<T::Out>;
    fn ffi(self) -> Vec<T::Out> {
        self.into_iter().map(Ffi::ffi).collect()
    }
}

impl<T: Ffi> Ffi for Option<T> {
    type Out = Option<T::Out>;
    fn ffi(self) -> Option<T::Out> {
        self.map(Ffi::ffi)
    }
}

impl<T: Ffi> Ffi for Box<T> {
    type Out = T::Out;
    fn ffi(self) -> T::Out {
        (*self).ffi()
    }
}

impl<K: Ffi, V: Ffi> Ffi for BTreeMap<K, V>
where
    K::Out: Eq + Hash,
{
    type Out = HashMap<K::Out, V::Out>;
    fn ffi(self) -> HashMap<K::Out, V::Out> {
        self.into_iter().map(|(k, v)| (k.ffi(), v.ffi())).collect()
    }
}

/// `record! { Name = core::Type { field: FfiType, renamed: FfiType => core_field, … } }`: a UniFFI record with the
/// fields listed and `Ffi` for the core type, converting each field with `Ffi::ffi`.
macro_rules! record {
    ($(#[$meta:meta])* $name:ident = $core:ty {
        $($(#[$fmeta:meta])* $field:ident : $ty:ty $(=> $src:ident)?),* $(,)?
    }) => {
        $(#[$meta])*
        #[derive(Debug, Clone, uniffi::Record)]
        pub struct $name {
            $($(#[$fmeta])* pub $field: $ty,)*
        }

        impl $crate::convert::Ffi for $core {
            type Out = $name;
            fn ffi(self) -> $name {
                let v = self;
                $name { $($field: $crate::convert::Ffi::ffi(record!(@src v, $field $(, $src)?)),)* }
            }
        }
    };
    (@src $v:ident, $field:ident) => { $v.$field };
    (@src $v:ident, $field:ident, $src:ident) => { $v.$src };
}

/// `simple_enum! { Name = core::Type { A, B } }`: a UniFFI enum without data, converted both ways.
macro_rules! simple_enum {
    ($(#[$meta:meta])* $name:ident = $core:ty { $($(#[$vmeta:meta])* $variant:ident),* $(,)? }) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, uniffi::Enum)]
        pub enum $name {
            $($(#[$vmeta])* $variant,)*
        }

        impl $crate::convert::Ffi for $core {
            type Out = $name;
            fn ffi(self) -> $name {
                match self {
                    $(Self::$variant => $name::$variant,)*
                }
            }
        }

        impl From<$name> for $core {
            fn from(v: $name) -> Self {
                match v {
                    $($name::$variant => Self::$variant,)*
                }
            }
        }
    };
}

pub(crate) use {record, simple_enum};
