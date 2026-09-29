use std::rc::Rc;

use gpui_kit::Refineable as _;
use gpui_kit::component::{ActiveTheme, TITLE_BAR_HEIGHT, TitleBar};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::{
    AnyElement, App, InteractiveElement, IntoElement, MouseButton, MouseMoveEvent, MouseUpEvent,
    ParentElement, Pixels, RenderOnce, StyleRefinement, Styled, Window, div, px, transparent_black,
};
use smallvec::SmallVec;

use super::surface::{SurfaceContext, SurfacePreset};

const DEFAULT_RESIZER_WIDTH: f32 = 6.0;
const DEFAULT_INSET: f32 = 4.0;

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum WindowLayoutMode {
    /// Sidebars float as inset panels over a full-window main area.
    #[default]
    FloatingPanels,
}

/// Window layout primitive with a transparent title bar, background, sidebar, and main slots.
///
/// Z-order from bottom to top is background, content, then title bar.
#[derive(IntoElement)]
pub struct WindowShell {
    layout_mode: WindowLayoutMode,
    sidebar_left: Option<AnyElement>,
    main: Option<AnyElement>,
    background: Option<AnyElement>,
    on_mouse_move: Option<Rc<dyn Fn(&MouseMoveEvent, &mut Window, &mut App)>>,
    on_mouse_up: Option<Rc<dyn Fn(&MouseUpEvent, &mut Window, &mut App)>>,
    style: StyleRefinement,
}

impl Default for WindowShell {
    fn default() -> Self {
        Self::new()
    }
}

impl WindowShell {
    pub fn new() -> Self {
        Self {
            layout_mode: WindowLayoutMode::default(),
            sidebar_left: None,
            main: None,
            background: None,
            on_mouse_move: None,
            on_mouse_up: None,
            style: StyleRefinement::default(),
        }
    }

    pub fn layout_mode(mut self, mode: WindowLayoutMode) -> Self {
        self.layout_mode = mode;
        self
    }

    pub fn sidebar_left(mut self, element: impl IntoElement) -> Self {
        self.sidebar_left = Some(element.into_any_element());
        self
    }

    pub fn main(mut self, element: impl IntoElement) -> Self {
        self.main = Some(element.into_any_element());
        self
    }

    pub fn background(mut self, element: impl IntoElement) -> Self {
        self.background = Some(element.into_any_element());
        self
    }

    pub fn on_mouse_move(
        mut self,
        handler: impl Fn(&MouseMoveEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.on_mouse_move = Some(Rc::new(handler));
        self
    }

    pub fn on_mouse_up(
        mut self,
        handler: impl Fn(&MouseUpEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.on_mouse_up = Some(Rc::new(handler));
        self
    }
}

impl Styled for WindowShell {
    fn style(&mut self) -> &mut StyleRefinement {
        &mut self.style
    }
}

impl RenderOnce for WindowShell {
    fn render(mut self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        let title_bar = TitleBar::new().bg(transparent_black()).border_b_0();

        let content_layer = match self.layout_mode {
            WindowLayoutMode::FloatingPanels => div()
                .id("window-shell-floating-layout")
                .absolute()
                .top_0()
                .left_0()
                .right_0()
                .bottom_0()
                .when_some(self.main, |element, main| {
                    element.child(div().size_full().child(main))
                })
                .when_some(self.sidebar_left, |element, sidebar| element.child(sidebar)),
        };

        let mut root = div().id("window-shell").size_full().relative();
        root.style().refine(&self.style);

        root.when_some(self.on_mouse_move, |element, handler| {
            element.on_mouse_move(move |event, window, cx| handler(event, window, cx))
        })
        .when_some(self.on_mouse_up, |element, handler| {
            element.on_mouse_up(MouseButton::Left, move |event, window, cx| {
                handler(event, window, cx)
            })
        })
        .when_some(self.background.take(), |element, background| {
            element.child(
                div()
                    .id("window-shell-background")
                    .absolute()
                    .inset_0()
                    .child(background),
            )
        })
        .child(content_layer)
        .child(
            div()
                .id("window-shell-titlebar")
                .absolute()
                .top_0()
                .left_0()
                .right_0()
                .h(TITLE_BAR_HEIGHT)
                .when(cfg!(not(target_os = "macos")), |element| element.occlude())
                .child(title_bar),
        )
    }
}

/// Resizable floating sidebar panel with a glass surface and a consumer-managed resize handle.
///
/// The consumer tracks drag state and handles mouse moves at the window level, because
/// `RenderOnce` components cannot register `cx.listener` handlers.
#[derive(IntoElement)]
pub struct SidebarShell {
    width: Pixels,
    resizer_width: Pixels,
    on_resize_start: Option<Rc<dyn Fn(Pixels, Pixels, &mut Window, &mut App)>>,
    on_resize_end: Option<Rc<dyn Fn(&mut Window, &mut App)>>,
    inset: Pixels,
    blur_enabled: bool,
    children: SmallVec<[AnyElement; 1]>,
    style: StyleRefinement,
}

impl SidebarShell {
    pub fn left(width: impl Into<Pixels>) -> Self {
        Self {
            width: width.into(),
            resizer_width: px(DEFAULT_RESIZER_WIDTH),
            on_resize_start: None,
            on_resize_end: None,
            inset: px(DEFAULT_INSET),
            blur_enabled: true,
            children: SmallVec::new(),
            style: StyleRefinement::default(),
        }
    }

    pub fn inset(mut self, inset: impl Into<Pixels>) -> Self {
        self.inset = inset.into();
        self
    }

    pub fn blur_enabled(mut self, enabled: bool) -> Self {
        self.blur_enabled = enabled;
        self
    }

    pub fn on_resize_start(
        mut self,
        callback: impl Fn(Pixels, Pixels, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.on_resize_start = Some(Rc::new(callback));
        self
    }

    pub fn on_resize_end(mut self, callback: impl Fn(&mut Window, &mut App) + 'static) -> Self {
        self.on_resize_end = Some(Rc::new(callback));
        self
    }
}

impl ParentElement for SidebarShell {
    fn extend(&mut self, elements: impl IntoIterator<Item = AnyElement>) {
        self.children.extend(elements);
    }
}

impl Styled for SidebarShell {
    fn style(&mut self) -> &mut StyleRefinement {
        &mut self.style
    }
}

impl RenderOnce for SidebarShell {
    fn render(self, window: &mut Window, cx: &mut App) -> impl IntoElement {
        let resizer_hover_background = cx.theme().foreground.alpha(0.20);

        let window_height = window.window_bounds().get_bounds().size.height;
        let inset = self.inset;
        let sidebar_height = (window_height - (inset + inset)).max(px(0.0));
        let sidebar_width = self.width;

        let sidebar_surface = SurfacePreset::panel()
            .wrap_with_bounds(
                div(),
                sidebar_width,
                sidebar_height,
                window,
                cx,
                SurfaceContext {
                    blur_enabled: self.blur_enabled,
                },
            )
            .children(self.children)
            .id("sidebar-shell-surface")
            .size_full();

        let resizer_half = self.resizer_width / 2.0;
        let resizer_left = sidebar_width - resizer_half;

        let on_resize_start = self.on_resize_start;
        let on_resize_end = self.on_resize_end;

        let mut outer = div()
            .id("sidebar-shell")
            .absolute()
            .top(inset)
            .bottom(inset)
            .w(sidebar_width)
            .left(inset)
            .child(
                div()
                    .id("sidebar-shell-shadow-wrapper")
                    .size_full()
                    .shadow_lg()
                    .child(sidebar_surface),
            )
            .child(
                div()
                    .id("sidebar-shell-resizer")
                    .absolute()
                    .top_0()
                    .bottom_0()
                    .left(resizer_left)
                    .w(self.resizer_width)
                    .rounded(px(999.0))
                    .bg(transparent_black())
                    .cursor_col_resize()
                    .hover(move |style| style.bg(resizer_hover_background))
                    .when_some(on_resize_start, move |element, callback| {
                        element.on_mouse_down(MouseButton::Left, move |event, window, cx| {
                            cx.stop_propagation();
                            callback(sidebar_width, event.position.x, window, cx);
                        })
                    })
                    .when_some(on_resize_end, move |element, callback| {
                        let callback_mouse_up = callback.clone();
                        element
                            .on_mouse_up(MouseButton::Left, move |_event, window, cx| {
                                callback_mouse_up(window, cx);
                            })
                            .on_mouse_up_out(MouseButton::Left, move |_event, window, cx| {
                                callback(window, cx);
                            })
                    }),
            );
        outer.style().refine(&self.style);
        outer
    }
}
