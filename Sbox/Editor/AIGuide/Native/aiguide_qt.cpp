// Project-local Qt 5 adapter. No Hammer binaries or engine sources are modified.
// Build with the Qt 5.15.2 MSVC x64 SDK matching s&box's shipped Qt libraries.
#define NOMINMAX
#include <Windows.h>
#include <QtWidgets/qwidget.h>
#include <QtWidgets/qmainwindow.h>
#include <QtWidgets/qgraphicsitem.h>
#include <QtWidgets/qdockwidget.h>
#include <QtWidgets/QtWidgets>
#include <QtCore/QtCore>
#include <QtGui/QtGui>
#include <cstdlib>
#include <stdexcept>
#include <functional>
#include <limits>
#include <future>
#include <memory>

namespace {
HWND dispatchWindow=nullptr;
constexpr UINT dispatchMessage=WM_APP+174;
LRESULT CALLBACK dispatchProc(HWND window,UINT message,WPARAM wparam,LPARAM lparam){
    if(message==dispatchMessage){std::unique_ptr<std::function<void()>> work(reinterpret_cast<std::function<void()>*>(lparam));(*work)();return 0;}
    return DefWindowProcW(window,message,wparam,lparam);
}
void ensureDispatchWindow(){
    if(dispatchWindow)return;WNDCLASSW wc{};wc.lpfnWndProc=dispatchProc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"MoonShooter.AIGuide.Dispatch";
    RegisterClassW(&wc);dispatchWindow=CreateWindowExW(0,wc.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,wc.hInstance,nullptr);
    if(!dispatchWindow)throw std::runtime_error("Could not create AI Guide GUI dispatcher");
}
void onGui(std::function<void()> work){
    if(QThread::currentThread()==qApp->thread()){ensureDispatchWindow();work();return;}
    if(!dispatchWindow)QMetaObject::invokeMethod(qApp,[]{ensureDispatchWindow();},Qt::BlockingQueuedConnection);
    auto done=std::make_shared<std::promise<void>>();auto future=done->get_future();
    auto task=new std::function<void()>([work=std::move(work),done]{try{work();done->set_value();}catch(...){done->set_exception(std::current_exception());}});
    if(!PostMessageW(dispatchWindow,dispatchMessage,0,reinterpret_cast<LPARAM>(task))){delete task;throw std::runtime_error("Could not post AI Guide GUI task");}
    future.get();
}
QString str(const QJsonObject& o, const char* k, QString d={}) {return o.value(k).isString()?o.value(k).toString():d;}
int num(const QJsonObject& o,const char*k,int d=0){return o.contains(k)?o.value(k).toInt(d):d;}
bool yes(const QJsonObject&o,const char*k,bool d=false){return o.contains(k)?o.value(k).toBool(d):d;}
QJsonArray rectJson(QRect r){return {r.x(),r.y(),r.width(),r.height()};}
QRect readRect(QJsonArray a){return {a[0].toInt(),a[1].toInt(),a[2].toInt(),a[3].toInt()};}
QJsonValue validUnicode(QJsonValue value){
    // Qt permits lone UTF-16 surrogates (some native icon labels contain them).
    // System.Text.Json rejects them; round-trip strings through UTF-8 at the boundary.
    if(value.isString())return QString::fromUtf8(value.toString().toUtf8());
    if(value.isArray()){QJsonArray out;for(auto v:value.toArray())out.append(validUnicode(v));return out;}
    if(value.isObject()){QJsonObject out;auto object=value.toObject();for(auto it=object.begin();it!=object.end();++it)out[QString::fromUtf8(it.key().toUtf8())]=validUnicode(it.value());return out;}
    return value;
}
QPair<int,Qt::KeyboardModifiers> parseKey(QString text){
    auto parts=text.trimmed().split('+');Qt::KeyboardModifiers mods;int value=0;
    for(auto part:parts){auto p=part.trimmed().toLower();if(p=="ctrl"||p=="control")mods|=Qt::ControlModifier;else if(p=="shift")mods|=Qt::ShiftModifier;else if(p=="alt")mods|=Qt::AltModifier;else if(p=="meta"||p=="win"||p=="super")mods|=Qt::MetaModifier;
        else if(p.size()==1)value=p[0].toUpper().unicode();
        else if(p.startsWith('f')&&p.mid(1).toInt()>=1&&p.mid(1).toInt()<=35)value=Qt::Key_F1+p.mid(1).toInt()-1;
        else {static QHash<QString,int> names{{"enter",Qt::Key_Return},{"return",Qt::Key_Return},{"escape",Qt::Key_Escape},{"esc",Qt::Key_Escape},{"tab",Qt::Key_Tab},{"space",Qt::Key_Space},{"backspace",Qt::Key_Backspace},{"delete",Qt::Key_Delete},{"del",Qt::Key_Delete},{"insert",Qt::Key_Insert},{"home",Qt::Key_Home},{"end",Qt::Key_End},{"pageup",Qt::Key_PageUp},{"pgup",Qt::Key_PageUp},{"pagedown",Qt::Key_PageDown},{"pgdown",Qt::Key_PageDown},{"left",Qt::Key_Left},{"right",Qt::Key_Right},{"up",Qt::Key_Up},{"down",Qt::Key_Down}};value=names.value(p,0);if(!value)throw std::runtime_error("Unsupported key name");}}
    if(!value)throw std::runtime_error("A key name is required");return {value,mods};
}
QString textOf(QWidget*w){
    for(auto key:{"text","title","windowTitle","currentText","placeholderText"}){auto v=w->property(key);if(v.isValid()&&!v.toString().isEmpty())return v.toString();}
    if(auto e=qobject_cast<QPlainTextEdit*>(w))return e->toPlainText().left(160);
    if(auto e=qobject_cast<QTextEdit*>(w))return e->toPlainText().left(160);
    return {};
}
bool own(QWidget*w){for(auto p=w;p;p=p->parentWidget())if(p->property("aiguideOwn").toBool())return true;return false;}
QString propertyOf(QWidget*w){for(auto p=w;p;p=p->parentWidget())for(auto key:{"aiguideProperty","propertyName","property"})if(p->property(key).isValid()&&!p->property(key).toString().isEmpty())return p->property(key).toString();return {};}
QString valueOf(QWidget*w){for(auto p=w;p;p=p->parentWidget())if(p->property("aiguideValue").isValid())return p->property("aiguideValue").toString();return w->property("value").toString();}
struct Element {
    QPointer<QWidget> w; QPersistentModelIndex index; QPointer<QAction> action;
    QString kind="control"; int item=-1; quintptr graphic=0;
    bool alive()const{
        if(!w)return false;if(kind=="tree")return index.isValid();if(kind=="menu")return !action.isNull();
        if(kind=="tab"){auto b=qobject_cast<QTabBar*>(w.data());return b&&item>=0&&item<b->count();}
        if(kind=="graphic"){auto v=qobject_cast<QGraphicsView*>(w.data());if(!v||!v->scene())return false;for(auto g:v->scene()->items())if(quintptr(g)==graphic)return true;return false;}
        return true;
    }
    QString label() const {
        if(!alive())return {};
        if(index.isValid())return index.data().toString();
        if(action)return action->text();
        if(kind=="tab")if(auto b=qobject_cast<QTabBar*>(w.data()))return b->tabText(item);
        if(kind=="graphic")if(auto v=qobject_cast<QGraphicsView*>(w.data()))for(auto g:v->scene()->items())if(quintptr(g)==graphic){if(auto t=dynamic_cast<QGraphicsTextItem*>(g))return t->toPlainText();return g->toolTip();}
        return textOf(w);
    }
    QRect rect()const {
        if(!alive())return {};
        QRect r=w->rect(); QWidget*canvas=w;
        if(index.isValid())if(auto v=qobject_cast<QAbstractItemView*>(w.data())){r=v->visualRect(index);canvas=v->viewport();}
        if(action){if(auto m=qobject_cast<QMenu*>(w.data()))r=m->actionGeometry(action);if(auto m=qobject_cast<QMenuBar*>(w.data()))r=m->actionGeometry(action);}
        if(kind=="tab")if(auto b=qobject_cast<QTabBar*>(w.data()))r=b->tabRect(item);
        if(kind=="graphic")if(auto v=qobject_cast<QGraphicsView*>(w.data())){r={};for(auto g:v->scene()->items())if(quintptr(g)==graphic){r=v->mapFromScene(g->sceneBoundingRect()).boundingRect();break;}canvas=v->viewport();}
        return {canvas->mapToGlobal(r.topLeft()),r.size()};
    }
    bool visible()const {
        if(!alive()||!w->isVisible()||rect().isEmpty())return false;
        if(action&&!action->isVisible())return false;
        auto r=rect();for(QWidget*p=w;p;p=p->parentWidget()){if(!p->isVisible())return false;r=r.intersected(QRect(p->mapToGlobal({0,0}),p->size()));if(r.isEmpty())return false;if(p->isWindow())break;}return true;
    }
};
class Overlay:public QWidget {
public:
    QVector<QPair<QRect,QString>> marks;QString caption;bool dim=false;
    Overlay(QWidget*p):QWidget(p){setProperty("aiguideOwn",true);setAttribute(Qt::WA_TransparentForMouseEvents);setAttribute(Qt::WA_NoSystemBackground);setAttribute(Qt::WA_TranslucentBackground);}
    void paintEvent(QPaintEvent*)override{
        QPainter p(this);p.setRenderHint(QPainter::Antialiasing);
        if(dim){QPainterPath path;path.addRect(rect());for(auto&m:marks)path.addRoundedRect(QRectF(mapFromGlobal(m.first.topLeft()),m.first.size()).adjusted(-4,-4,4,4),5,5);path.setFillRule(Qt::OddEvenFill);p.fillPath(path,QColor(0,0,0,115));}
        for(auto&m:marks){QRect r(mapFromGlobal(m.first.topLeft()),m.first.size());p.setPen(QPen(QColor("#ffb74d"),3));p.setBrush(Qt::NoBrush);p.drawRoundedRect(r.adjusted(-3,-3,3,3),4,4);if(!m.second.isEmpty()){QRect tag(r.topLeft()-QPoint(0,23),QSize(qMax(28,m.second.size()*9),22));p.fillRect(tag,QColor("#ffb74d"));p.setPen(Qt::black);p.drawText(tag,Qt::AlignCenter,m.second);}}
    }
};
class Bridge:public QObject {
public:
    QHash<QObject*,int> ids;QHash<QString,Element> refs;int seq=0;
    QSet<const QEvent*> synthetic;QJsonArray events,records,steps;QJsonObject context,lastGuide;
    QHash<QWidget*,QPointer<Overlay>> overlays;QPointer<QWidget>bubble;QPointer<QLabel>message;
    QJsonArray highlights;QString markText,title,state="idle";int step=0;bool dim=true; qint64 markUntil=0,entered=0;bool preSatisfied=false,restartArmed=false;
    QTimer timer;bool replay=false;int eventSequence=0,actionSequence=0;QHash<int,QJsonObject> actionResults;
    QPointer<QWidget>fixture,hovered;
    Bridge(){qApp->installEventFilter(this);connect(&timer,&QTimer::timeout,this,[this]{tick();});timer.start(80);}
    ~Bridge(){clear();}
    QString ref(Element e){if(!e.w)return {};int id=ids.value(e.w,0);if(!id){id=++seq;ids[e.w]=id;connect(e.w,&QObject::destroyed,this,[this,obj=e.w.data()]{ids.remove(obj);});}QString r="e"+QString::number(id);if(e.kind!="control"){r+='.'+e.kind+':';if(e.index.isValid()){QStringList rows;for(auto i=QModelIndex(e.index);i.isValid();i=i.parent())rows.prepend(QString::number(i.row()));r+=rows.join(',')+':'+QString::number(e.index.column());}else if(e.action)r+=QString::number(quintptr(e.action.data()),16);else r+=QString::number(e.graphic?e.graphic:e.item);}refs[r]=e;return r;}
    QList<QWidget*> roots(){auto list=qApp->topLevelWidgets();list.erase(std::remove_if(list.begin(),list.end(),[](QWidget*w){return own(w)||w->windowType()==Qt::ToolTip;}),list.end());std::sort(list.begin(),list.end(),[](QWidget*a,QWidget*b){return (QString(a->metaObject()->className())+a->windowTitle())<(QString(b->metaObject()->className())+b->windowTitle());});return list;}
    QVector<Element> elements(QWidget*root=nullptr,int maxItems=40){
        QVector<Element> out;QList<QWidget*> pending=root?QList<QWidget*>{root}:roots();QSet<QWidget*>seen;
        for(int n=0;n<pending.size()&&out.size()<20000;n++){
            QWidget*w=pending[n];if(!w||seen.contains(w)||own(w))continue;seen.insert(w);out.append(Element{w});
            if(auto v=qobject_cast<QAbstractItemView*>(w))if(!qobject_cast<QHeaderView*>(w)&&v->model()){
                int count=0;std::function<void(QModelIndex,int)>visit=[&](QModelIndex parent,int depth){if(depth>16||count>=maxItems)return;for(int row=0;row<v->model()->rowCount(parent)&&count<maxItems;row++){for(int col=0;col<qMin(12,v->model()->columnCount(parent))&&count<maxItems;col++){auto ix=v->model()->index(row,col,parent);if(ix.isValid()){Element e;e.w=w;e.kind="tree";e.index=ix;out.append(e);count++;}}visit(v->model()->index(row,0,parent),depth+1);}};visit({},0);
            }
            if(auto b=qobject_cast<QTabBar*>(w))for(int i=0;i<qMin(b->count(),maxItems);i++){Element e;e.w=w;e.kind="tab";e.item=i;out.append(e);}
            if(qobject_cast<QMenu*>(w)||qobject_cast<QMenuBar*>(w))for(auto a:w->actions()){Element e;e.w=w;e.kind="menu";e.action=a;out.append(e);}
            if(auto v=qobject_cast<QGraphicsView*>(w))if(v->scene()){int count=0;for(auto g:v->scene()->items()){if(count++>=maxItems)break;Element e;e.w=w;e.kind="graphic";e.graphic=quintptr(g);out.append(e);}}
            for(auto child:w->findChildren<QWidget*>(QString(),Qt::FindDirectChildrenOnly))pending.append(child);
        }return out;
    }
    QJsonObject identity(QWidget*w){QJsonObject o{{"class",w->metaObject()->className()},{"name",w->objectName()}};int nth=0;auto siblings=w->parentWidget()?w->parentWidget()->findChildren<QWidget*>(QString(),Qt::FindDirectChildrenOnly):roots();for(auto s:siblings){if(s==w)break;if(QString(s->metaObject()->className())==QString(w->metaObject()->className())&&s->objectName()==w->objectName())nth++;}o["nth"]=nth;return o;}
    QString locator(Element e){QJsonArray path;for(auto p=e.w.data();p;p=p->parentWidget())path.prepend(identity(p));QJsonObject o{{"path",path},{"kind",e.kind},{"text",e.label()}};if(e.index.isValid()){QJsonArray rows;for(auto i=QModelIndex(e.index);i.isValid();i=i.parent())rows.prepend(i.row());o["rows"]=rows;o["column"]=e.index.column();}if(e.kind=="tab")o["item"]=e.item;return "/qt/"+QJsonDocument(o).toJson(QJsonDocument::Compact).toBase64(QByteArray::Base64UrlEncoding|QByteArray::OmitTrailingEquals);}
    bool segment(QWidget*w,QJsonObject s){return QString(w->metaObject()->className())==str(s,"class")&&w->objectName()==str(s,"name")&&(!s.contains("text")||textOf(w).left(100)==str(s,"text"));}
    Element locate(QString value){auto doc=QJsonDocument::fromJson(QByteArray::fromBase64(value.mid(4).toLatin1(),QByteArray::Base64UrlEncoding));auto spec=doc.object();auto path=spec["path"].toArray();QWidget*w=nullptr;for(auto v:path){QList<QWidget*> candidates=w?w->findChildren<QWidget*>(QString(),Qt::FindDirectChildrenOnly):roots();auto s=v.toObject();int n=0;QWidget*next=nullptr;for(auto c:candidates)if(segment(c,s)&&n++==num(s,"nth")){next=c;break;}if(!next)return {};w=next;}if(!w)return {};QString kind=str(spec,"kind","control");if(kind=="control")return Element{w};auto all=elements(w,2000);QVector<Element>matches;for(auto e:all)if(e.w==w&&e.kind==kind&&e.label()==str(spec,"text"))matches.append(e);if(matches.size()==1)return matches[0];for(auto e:matches){if(kind=="tab"&&e.item==num(spec,"item"))return e;if(kind=="tree"){QJsonArray rows;for(auto i=QModelIndex(e.index);i.isValid();i=i.parent())rows.prepend(i.row());if(rows==spec["rows"].toArray()&&e.index.column()==num(spec,"column"))return e;}}return {};}
    bool match(Element e,QJsonObject q){if(!e.w)return false;auto contains=[&](QString have,QString want){return want.isEmpty()||(yes(q,"exact")?have==want:have.contains(want,Qt::CaseInsensitive));};return contains(e.label(),str(q,"text"))&&(str(q,"class").isEmpty()||contains(e.w->metaObject()->className(),str(q,"class"))||e.w->managedClassNames.contains(str(q,"class")))&&(str(q,"name").isEmpty()||e.w->objectName()==str(q,"name"))&&contains(e.w->toolTip(),str(q,"tooltip"))&&(str(q,"kind","any")=="any"||e.kind==str(q,"kind"))&&(!yes(q,"visible_only",true)||e.visible())&&contains(propertyOf(e.w),str(q,"property"));}
    Element resolve(QJsonValue target){
        if(target.isString()){auto s=target.toString();if(s.startsWith("/qt/"))return locate(s);if(refs.contains(s)){auto e=refs.value(s);if(e.alive())return e;return {};}target=QJsonObject{{"text",s}};}
        if(!target.isObject())return {};
        auto q=target.toObject();QWidget*under=nullptr;if(q.contains("under"))under=resolve(q["under"]).w;
        QVector<Element>matches;for(auto e:elements(under,2000))if(match(e,q))matches.append(e);
        if(matches.size()==1)return matches[0];
        if(matches.size()>1){QVector<Element>exact;for(auto e:matches)if(e.label().remove('&')==str(q,"text"))exact.append(e);if(exact.size()==1)return exact[0];throw std::runtime_error(QString("Ambiguous UI target (%1 matches); use ui_find then ref or locator").arg(matches.size()).toStdString());}return {};
    }
    QJsonObject describe(Element e){if(!e.alive())return {{"ok",false},{"error","Target not found"}};QJsonArray why;
        for(auto p=e.w.data();p;p=p->parentWidget())if(auto stack=qobject_cast<QStackedWidget*>(p->parentWidget()))if(stack->currentWidget()!=p)why.append(QJsonObject{{"type","inactive_tab"},{"container",ref(Element{stack})}});
        if(e.index.isValid())if(auto tree=qobject_cast<QTreeView*>(e.w.data()))for(auto p=e.index.parent();p.isValid();p=p.parent())if(!tree->isExpanded(p))why.append(QJsonObject{{"type","collapsed_parent"},{"text",p.data().toString()}});
        if(!e.w->isVisible())why.append(QJsonObject{{"type",e.w->window()->isVisible()?"hidden_ancestor":"window_hidden"}});else if(!e.visible()&&why.isEmpty())why.append(QJsonObject{{"type","scrolled_out_or_clipped"}});
        if(auto modal=qApp->activeModalWidget())if(modal!=e.w->window())why.append(QJsonObject{{"type","blocked_by_modal"},{"window",ref(Element{modal})}});
        return {{"ok",true},{"ref",ref(e)},{"locator",locator(e)},{"class",e.w->metaObject()->className()},{"name",e.w->objectName()},{"kind",e.kind},{"text",e.label().left(500)},{"tooltip",e.w->toolTip()},{"rect",rectJson(e.rect())},{"visible",e.visible()},{"enabled",e.w->isEnabled()&&(!e.action||e.action->isEnabled())},{"focused",e.w->hasFocus()},{"window",ref(Element{e.w->window()})},{"hidden_because",why},{"property",propertyOf(e.w)},{"value",valueOf(e.w)},{"managed_classes",QJsonArray::fromStringList(e.w->managedClassNames)}};}
    void log(QJsonObject o){o["at"]=QDateTime::currentMSecsSinceEpoch();o["seq"]=++eventSequence;events.append(o);while(events.size()>100)events.removeFirst();}
    bool eventFilter(QObject*obj,QEvent*event)override{
        auto w=qobject_cast<QWidget*>(obj);if(!w||own(w))return false;
        bool agent=synthetic.contains(event);
        if(event->type()==QEvent::MouseButtonRelease){auto m=static_cast<QMouseEvent*>(event);if(m->button()==Qt::LeftButton){Element clicked{w};log({{"event","click"},{"agent",agent},{"target",describe(clicked)},{"x",m->globalPos().x()},{"y",m->globalPos().y()}});if(!agent&&state=="running"&&step<steps.size()){auto s=steps[step].toObject();try{auto e=resolve(s["target"]);if(e.w&&e.visible()&&e.rect().adjusted(-3,-3,3,3).contains(m->globalPos())&&(!s.contains("until")||s["until"].isNull()||s["until"].toString()=="click"||preSatisfied))advance("click");}catch(...){} }}}
        if(event->type()==QEvent::KeyPress){auto k=static_cast<QKeyEvent*>(event);QString key=QKeySequence(k->modifiers()|k->key()).toString(QKeySequence::PortableText).toLower();log({{"event","key"},{"keys",key},{"agent",agent}});if(!agent&&state=="running"&&step<steps.size()){auto until=steps[step].toObject()["until"];if(keyCondition(until,key))advance("key");}}
        return false;
    }
    bool keyCondition(QJsonValue u,QString key){auto o=u.toObject();if(str(o,"key").toLower()==key)return true;for(auto a:o["any"].toArray())if(keyCondition(a,key))return true;return false;}
    void send(QWidget*w,QEvent*event){synthetic.insert(event);QApplication::sendEvent(w,event);synthetic.remove(event);}
    void mouse(QWidget*w,QPoint global,QEvent::Type type,Qt::MouseButton button=Qt::NoButton,Qt::MouseButtons buttons={},Qt::KeyboardModifiers mods={}){QMouseEvent e(type,w->mapFromGlobal(global),w->window()->mapFromGlobal(global),global,button,buttons,mods,Qt::MouseEventSynthesizedByApplication);send(w,&e);}
    void key(QWidget*w,int key,Qt::KeyboardModifiers mods={},QString text={}){QKeyEvent down(QEvent::KeyPress,key,mods,text),up(QEvent::KeyRelease,key,mods,text);send(w,&down);send(w,&up);}
    QWidget*focus(){return qApp->activePopupWidget()?qApp->activePopupWidget():qApp->focusWidget()?qApp->focusWidget():(qApp->activeModalWidget()?qApp->activeModalWidget():qApp->activeWindow());}
    QJsonObject act(QJsonObject a){
        auto action=str(a,"action");Element e;if(a.contains("target"))e=resolve(a["target"]);
        QWidget*w=e.w;if(!w&&a.contains("target"))throw std::runtime_error("UI target not found");
        if(e.w&&(!e.w->isEnabled()||(e.action&&!e.action->isEnabled())))throw std::runtime_error("Target is disabled");
        if(!w&&a.contains("window"))w=resolve(a["window"]).w;if(!w)w=focus();if(!w)throw std::runtime_error("No target window");
        if(auto modal=qApp->activeModalWidget())if(modal!=w->window()&&!modal->isAncestorOf(w))throw std::runtime_error("A modal dialog blocks the target; operate the dialog first");
        if(e.w&&!e.visible()&&action!="focus"){if(e.index.isValid())if(auto v=qobject_cast<QAbstractItemView*>(w))v->scrollTo(e.index);if(!e.visible())throw std::runtime_error("Target is hidden; ui_describe explains why");}
        auto point=[](QJsonObject o){return QPoint(qRound(o["x"].toDouble()),qRound(o["y"].toDouble()));};
        QPoint p=e.w?e.rect().center():w->window()->mapToGlobal(point(a));if(e.w&&a.contains("x"))p=w->window()->mapToGlobal(point(a));
        auto mods=a["modifiers"].toObject();Qt::KeyboardModifiers flags;if(yes(mods,"ctrl"))flags|=Qt::ControlModifier;if(yes(mods,"shift"))flags|=Qt::ShiftModifier;if(yes(mods,"alt"))flags|=Qt::AltModifier;if(yes(mods,"meta"))flags|=Qt::MetaModifier;
        QPoint end=p;if(action=="drag"){auto to=a["to"];if(to.isObject()&&to.toObject().contains("x"))end=w->window()->mapToGlobal(point(to.toObject()));else{auto dest=resolve(to);if(!dest.w||!dest.visible())throw std::runtime_error("Drag destination not found or hidden");end=dest.rect().center();}}
        if(action=="key")parseKey(str(a,"keys"));
        QWidget*target=w;if(e.index.isValid())if(auto v=qobject_cast<QAbstractItemView*>(w))target=v->viewport();if(e.kind=="graphic")if(auto v=qobject_cast<QGraphicsView*>(w))target=v->viewport();
        if(!e.w&&a.contains("x")&&action!="key"&&action!="type"&&action!="paste"&&action!="focus"){
            if(auto child=w->window()->childAt(w->window()->mapFromGlobal(p)))target=child;
            e.w=target;
        }
        if(yes(a,"show")&&e.w){highlights={locator(e)};markText=str(a,"text",action);markUntil=QDateTime::currentMSecsSinceEpoch()+1000;tick();}
        int actionId=++actionSequence;actionResults[actionId]={{"status","queued"},{"ok",true}};
        auto work=[this,a,action,target=QPointer<QWidget>(target),e,p,end,flags,actionId]{
            if(!target){actionResults[actionId]={{"status","failed"},{"ok",false},{"error","Target was destroyed before delivery"}};return;}
            actionResults[actionId]={{"status","delivering"},{"ok",true}};
            struct Finish{Bridge*b;int id;bool deferred=false;~Finish(){if(!deferred&&b->actionResults[id]["status"].toString()=="delivering")b->actionResults[id]={{"status","completed"},{"ok",true}};}} finish{this,actionId};try{
            if(!qApp->activePopupWidget())QApplication::setActiveWindow(target->window());
            if(e.action&&(action=="click"||action=="double_click")){
                if(auto menu=e.action->menu())menu->popup(e.rect().bottomLeft());else e.action->trigger();
                return;
            }
            if(action=="focus"){target->setFocus();return;}
            if(action=="key"){if(e.w)target->setFocus();auto combo=parseKey(str(a,"keys"));key(e.w?target.data():focus(),combo.first,combo.second);return;}
            if(action=="type"||action=="paste"){
                target->setFocus();if(action=="paste"){qApp->clipboard()->setText(str(a,"text"));key(target,Qt::Key_A,Qt::ControlModifier);key(target,Qt::Key_V,Qt::ControlModifier);}else{auto text=str(a,"text");for(int i=0;i<text.size();i++){if(text[i]=='\n')key(target,Qt::Key_Return);else{int count=text.at(i).isHighSurrogate()&&i+1<text.size()&&text.at(i+1).isLowSurrogate()?2:1;if(count==2){QInputMethodEvent input;input.setCommitString(text.mid(i,count));send(target,&input);}else key(target,0,{},text.mid(i,count));i+=count-1;}}}return;
            }
            if(hovered&&hovered!=target){QEvent leave(QEvent::Leave);send(hovered,&leave);hovered->setAttribute(Qt::WA_UnderMouse,false);}hovered=target;
            QEvent enter(QEvent::Enter);send(target,&enter);target->setAttribute(Qt::WA_UnderMouse,true);mouse(target,p,QEvent::MouseMove,Qt::NoButton,{},flags);
            if(action=="hover")return;
            if(action=="scroll"){QWheelEvent wheel(target->mapFromGlobal(p),p,{},QPoint(0,-120*num(a,"amount",3)),Qt::NoButton,flags,Qt::NoScrollPhase,false,Qt::MouseEventSynthesizedByApplication);send(target,&wheel);return;}
            Qt::MouseButton button=action=="right_click"?Qt::RightButton:action=="middle_click"?Qt::MiddleButton:Qt::LeftButton;
            mouse(target,p,QEvent::MouseButtonPress,button,button,flags);
            if(action=="drag"){
                // Queue release before moving, so nested Qt drag loops can still receive it.
                finish.deferred=true;
                QTimer::singleShot(210,qApp,[this,target,end,button,flags,actionId]{if(target){mouse(target,end,QEvent::MouseButtonRelease,button,{},flags);actionResults[actionId]={{"status","completed"},{"ok",true}};}else actionResults[actionId]={{"status","failed"},{"ok",false},{"error","Drag source was destroyed"}};});
                for(int i=1;i<=12;i++)QTimer::singleShot(i*15,qApp,[this,target,p,end,i,button,flags]{if(target)mouse(target,p+(end-p)*i/12,QEvent::MouseMove,Qt::NoButton,button,flags);});
            }else{mouse(target,p,QEvent::MouseButtonRelease,button,{},flags);if(action=="double_click"){mouse(target,p,QEvent::MouseButtonDblClick,button,button,flags);mouse(target,p,QEvent::MouseButtonRelease,button,{},flags);}if(action=="right_click"){QContextMenuEvent menu(QContextMenuEvent::Mouse,target->mapFromGlobal(p),p,flags);send(target,&menu);}}
        }catch(const std::exception&err){actionResults[actionId]={{"status","failed"},{"ok",false},{"error",err.what()}};log({{"event","action_error"},{"action_id",actionId},{"error",err.what()}});}};
        if(!QStringList{"click","double_click","right_click","middle_click","hover","scroll","drag","type","paste","key","focus"}.contains(action))throw std::runtime_error("Unknown UI action");
        // Defer delivery so modal exec() cannot block this RPC's response.
        QTimer::singleShot(yes(a,"show")?350:0,qApp,work);
        QJsonObject record=a;if(e.w){record["target"]=locator(e);record["target_info"]=describe(e);}record["at"]=QDateTime::currentMSecsSinceEpoch();records.append(record);
        while(actionResults.size()>200)actionResults.remove(actionSequence-200);
        return {{"ok",true},{"queued",true},{"action_id",actionId},{"cursor_moved",false},{"record",record}};
    }
    bool condition(QJsonValue u){
        auto o=u.toObject();if(o.isEmpty())return false;
        for(auto c:o["any"].toArray())if(condition(c))return true;
        for(auto kind:{"exists","visible"})if(o.contains(kind)){auto target=o[kind];if(target.isObject()){auto q=target.toObject();q["visible_only"]=false;target=q;}auto e=resolve(target);return e.w&&(QString(kind)=="exists"||e.visible());}
        if(o.contains("text_contains")){auto s=o["text_contains"].toObject();auto e=resolve(s["target"]);return e.w&&e.label().contains(str(s,"text"));}
        for(auto k:{"selected","inspecting"})if(o.contains(k)){for(auto v:context[k].toArray())if(v.toString()==o[k].toString())return true;return false;}
        if(o.contains("main_screen"))return context["main_screen"]==o["main_screen"];
        if(o.contains("file_saved")){
            auto s=o["file_saved"].toObject();auto root=str(context,"project");if(root.isEmpty())return false;QList<QString> pending{root};
            for(int i=0;i<pending.size();i++){QDir dir(pending[i]);for(auto info:dir.entryInfoList(QStringList{"*."+str(s,"ext")},QDir::Files|QDir::NoSymLinks)){if(info.lastModified().toMSecsSinceEpoch()<entered)continue;QFile f(info.absoluteFilePath());if(f.open(QIODevice::ReadOnly)&&(str(s,"contains").isEmpty()||QString::fromUtf8(f.readAll()).contains(str(s,"contains"))))return true;}
                for(auto info:dir.entryInfoList(QDir::Dirs|QDir::NoDotAndDotDot|QDir::NoSymLinks)){auto rel=QDir(root).relativeFilePath(info.absoluteFilePath());if(QStringList{".ai_guide",".ai_guide_bridge",".betterlore",".git",".sbox",".source2",".intermediate","obj","bin","node_modules"}.contains(info.fileName())||rel.startsWith("Art/build",Qt::CaseInsensitive)||rel.startsWith("Art/baseline",Qt::CaseInsensitive))continue;pending.append(info.absoluteFilePath());}}
            return false;
        }
        return false;
    }
    void enterStep(){entered=QDateTime::currentMSecsSinceEpoch();preSatisfied=false;if(step<steps.size()){try{preSatisfied=condition(steps[step].toObject()["until"]);}catch(...){}auto s=steps[step].toObject();if(s.contains("clipboard"))qApp->clipboard()->setText(str(s,"clipboard"));}log({{"event","step_enter"},{"index",step}});}
    void advance(QString why){if(state!="running")return;log({{"event","advanced"},{"index",step},{"how",why}});step++;if(step>=steps.size()){state="completed";clearOverlays();if(bubble)bubble->hide();}else enterStep();}
    void clearOverlays(){for(auto o:overlays)if(o)o->hide();}
    void clear(){clearOverlays();if(bubble)bubble->hide();highlights={};state="stopped";}
    void bubbleFor(QWidget*window,QString text,QRect target){
        if(!bubble||bubble->parentWidget()!=window){if(bubble)bubble->deleteLater();auto b=new QFrame(window,Qt::Tool|Qt::FramelessWindowHint);b->setProperty("aiguideOwn",true);b->setAttribute(Qt::WA_ShowWithoutActivating);b->setStyleSheet("QFrame{background:#202936;color:#ffffff;border:1px solid #ffb74d;border-radius:6px} QLabel{border:none} QPushButton{padding:5px;color:white;background:#394557;border:1px solid #657184}");auto l=new QVBoxLayout(b);auto label=new QLabel(b);label->setWordWrap(true);label->setMinimumWidth(270);label->setMaximumWidth(340);l->addWidget(label);message=label;auto row=new QHBoxLayout;for(auto pair:QVector<QPair<QString,QString>>{{"prev","上一步"},{"next","下一步"},{"restart","从头开始"},{"stop","退出"}}){auto btn=new QPushButton(pair.second,b);row->addWidget(btn);connect(btn,&QPushButton::clicked,this,[this,action=pair.first]{control(action,true);});}l->addLayout(row);bubble=b;}
        message->setText(text);bubble->adjustSize();QRect available=window->screen()->availableGeometry();QPoint p=target.isEmpty()?window->mapToGlobal(QPoint(20,40)):target.bottomRight()+QPoint(12,12);p.setX(qBound(available.left(),p.x(),qMax(available.left(),available.right()-bubble->width())));p.setY(qBound(available.top(),p.y(),qMax(available.top(),available.bottom()-bubble->height())));bubble->move(p);bubble->show();bubble->raise();
    }
    QJsonObject status(){return {{"state",state},{"title",title},{"index",step},{"step",step+1},{"total",steps.size()},{"steps",steps},{"log",events},{"waiting",state=="running"&&lastGuide["waiting"].toBool()},{"restart_armed",restartArmed}};}
    QJsonObject control(QString action,bool human=false){
        if(action=="stop"){clear();return status();}if(action=="next")advance(human?"user_next":"agent_next");
        else if(action=="prev"){step=qMax(0,step-1);state="running";enterStep();}
        else if(action=="restart"||action=="restart_clean"){if(human&&!restartArmed){restartArmed=true;log({{"event","restart_armed"}});}else {restartArmed=false;log({{"event","restart_requested"}});if(!human){step=0;state="running";enterStep();}}}
        else if(action=="do_step"){if(step>=steps.size())throw std::runtime_error("No current step");auto s=steps[step].toObject();auto a=s["action"].isObject()?s["action"].toObject():QJsonObject{{"action",str(s,"action","click")}};a["target"]=s["target"];act(a);advance("agent_did_step");}
        else if(action!="next")throw std::runtime_error("Unknown guide control");return status();
    }
    void tick(){
        clearOverlays();QVector<QPair<Element,QString>> marks;QWidget*root=nullptr;QString caption;
        if(markUntil>QDateTime::currentMSecsSinceEpoch())for(auto t:highlights){try{auto e=resolve(t);if(e.w){marks.append({e,QString::number(marks.size()+1)});root=e.w->window();}}catch(...){}}
        if(state=="running"&&step<steps.size()){
            auto s=steps[step].toObject();Element e;try{e=resolve(s["target"]);if(!preSatisfied&&condition(s["until"])) {advance("condition");return;}}catch(...){}
            if(e.w&&e.visible()){marks.append({e,QString::number(step+1)});root=e.w->window();lastGuide["waiting"]=false;}else{lastGuide["waiting"]=true;if(yes(lastGuide,"auto_scroll",true)&&e.index.isValid())if(auto v=qobject_cast<QAbstractItemView*>(e.w.data()))v->scrollTo(e.index);root=qApp->activeModalWidget()?qApp->activeModalWidget():qApp->activeWindow();}
            caption=QString("%1  %2/%3\n%4%5").arg(title).arg(step+1).arg(steps.size()).arg(str(s,"text")).arg(preSatisfied?"\n条件已满足，点击目标或下一步继续。":(!e.w||!e.visible()?"\n等待目标显露…":""));
        }else if(!marks.isEmpty())caption=markText;
        for(auto mark:marks){auto e=mark.first;auto win=e.w->window();auto overlay=overlays.value(win);if(!overlay){overlay=new Overlay(win);overlays[win]=overlay;}overlay->dim=state=="running"&&dim;overlay->setGeometry(win->rect());overlay->marks.append({e.rect(),mark.second});overlay->show();overlay->raise();overlay->update();}
        // Clear stale marks once per frame, keeping all targets in the same window.
        for(auto overlay:overlays)if(overlay){auto old=overlay->marks;overlay->marks.clear();for(auto mark:marks)if(mark.first.w->window()==overlay->parentWidget())overlay->marks.append({mark.first.rect(),mark.second});}
        if(root&&!own(root)&&!caption.isEmpty())bubbleFor(root,caption,marks.isEmpty()?QRect():marks.last().first.rect());else if(bubble)bubble->hide();
    }
    QJsonValue call(QString command,QJsonObject a){
        if(command=="test_fixture"){
            if(fixture){fixture->show();fixture->raise();return describe(Element{fixture});}
            auto win=new QMainWindow;win->setObjectName("AIGuideFixture");win->setWindowTitle("AI Guide native test");win->resize(640,540);fixture=win;
            auto center=new QWidget(win);auto layout=new QVBoxLayout(center);win->setCentralWidget(center);
            auto count=new QLabel("Count: 0",center);count->setObjectName("guideCount");count->setProperty("count",0);layout->addWidget(count);
            auto button=new QPushButton("计数 +1",center);button->setObjectName("guideIncrement");layout->addWidget(button);connect(button,&QPushButton::clicked,count,[count]{int n=count->property("count").toInt()+1;count->setProperty("count",n);count->setText("Count: "+QString::number(n));});
            auto edit=new QLineEdit(center);edit->setObjectName("guideText");layout->addWidget(edit);
            auto slider=new QSlider(Qt::Horizontal,center);slider->setObjectName("guideSlider");slider->setRange(0,100);slider->setValue(50);layout->addWidget(slider);
            auto tree=new QTreeWidget(center);tree->setObjectName("guideTree");tree->setHeaderLabel("Objects");auto player=new QTreeWidgetItem(tree,QStringList{"Player"});new QTreeWidgetItem(player,QStringList{"Body"});layout->addWidget(tree);
            auto tabs=new QTabWidget(center);tabs->setObjectName("guideTabs");tabs->addTab(new QLabel("First page",tabs),"One");tabs->addTab(new QLabel("Second page",tabs),"Two");layout->addWidget(tabs);
            auto bar=new QMenuBar(center);layout->insertWidget(0,bar);auto menu=bar->addMenu("Test");auto modal=menu->addAction("Open modal");connect(modal,&QAction::triggered,win,[this,win]{QDialog dialog(win);dialog.setObjectName("guideModal");dialog.setWindowTitle("AI Guide modal");QVBoxLayout layout(&dialog);QLineEdit edit(&dialog);edit.setObjectName("modalText");layout.addWidget(&edit);QPushButton close("Close modal",&dialog);close.setObjectName("modalClose");layout.addWidget(&close);QObject::connect(&close,&QPushButton::clicked,&dialog,&QDialog::accept);dialog.exec();});
            win->show();return describe(Element{win});
        }
        if(command=="test_user_click"){
            auto e=resolve(a["target"]);if(!e.w)throw std::runtime_error("Fixture target not found");QPoint p=e.rect().center();QMouseEvent down(QEvent::MouseButtonPress,e.w->mapFromGlobal(p),p,Qt::LeftButton,Qt::LeftButton,{}),up(QEvent::MouseButtonRelease,e.w->mapFromGlobal(p),p,Qt::LeftButton,{},{});QApplication::sendEvent(e.w,&down);QApplication::sendEvent(e.w,&up);return true;
        }
        if(command=="test_user_key"){
            auto e=resolve(a["target"]);if(!e.w)throw std::runtime_error("Fixture target not found");auto combo=parseKey(str(a,"keys"));QKeyEvent down(QEvent::KeyPress,combo.first,combo.second),up(QEvent::KeyRelease,combo.first,combo.second);QApplication::sendEvent(e.w,&down);QApplication::sendEvent(e.w,&up);return true;
        }
        if(command=="test_close_modal"){auto dialog=qobject_cast<QDialog*>(qApp->activeModalWidget());if(dialog&&dialog->objectName()=="guideModal"){dialog->reject();return true;}return false;}
        if(command=="test_close"){if(fixture){if(auto modal=qApp->activeModalWidget())if(fixture->isAncestorOf(modal))throw std::runtime_error("Close the fixture modal before destroying its owner");fixture->close();fixture->deleteLater();fixture=nullptr;}return true;}
        if(command=="info")return QJsonObject{{"qt",qVersion()},{"native_ui",true},{"cursor_independent",true},{"modal_safe",true},{"virtual_items",QJsonArray{"tree","tab","menu","graphic"}}};
        if(command=="context"){context=a;return true;}
        if(command=="events"){auto copy=events;if(yes(a,"drain"))events={};return copy;}
        if(command=="state"){QJsonArray out;for(auto w:roots())if(w->isVisible())out.append(describe(Element{w}));return QJsonObject{{"windows",out},{"modal",qApp->activeModalWidget()?QJsonValue(describe(Element{qApp->activeModalWidget()})):QJsonValue()},{"focus",focus()?describe(Element{focus()}):QJsonObject()},{"guide",status()}};}
        if(command=="snapshot"||command=="find"){
            QWidget*under=nullptr;if(a.contains("root"))under=resolve(a["root"]).w;if(a.contains("under"))under=resolve(a["under"]).w;
            QJsonArray out;int total=0;for(auto e:elements(under,num(a,"max_items",40))){if(command=="find"?!match(e,a):(yes(a,"visible_only",true)&&!e.visible()))continue;total++;if(out.size()<num(a,command=="find"?"limit":"max_nodes",command=="find"?20:600))out.append(describe(e));}return QJsonObject{{"total",total},{"elements",out}};
        }
        if(command=="describe")return describe(resolve(a["target"]));
        if(command=="act")return act(a);
        if(command=="action_status")return actionResults.value(num(a,"id"),{{"status","unknown"},{"ok",false}});
        if(command=="highlight"){highlights=a["targets"].toArray();if(highlights.isEmpty()&&a.contains("target"))highlights.append(a["target"]);markText=str(a,"text");auto duration=num(a,"duration_ms",6000);markUntil=duration==0?std::numeric_limits<qint64>::max():QDateTime::currentMSecsSinceEpoch()+duration;tick();return QJsonObject{{"ok",true},{"targets",highlights.size()}};}
        if(command=="guide"){steps=a["steps"].toArray();if(steps.isEmpty())throw std::runtime_error("Guide requires steps");lastGuide=a;lastGuide["waiting"]=false;restartArmed=false;title=str(a,"title","AI Guide");step=0;dim=yes(a,"dim",true);state="running";highlights={};enterStep();tick();return status();}
        if(command=="guide_status")return status();
        if(command=="guide_control")return control(str(a,"action"));
        if(command=="clear"){clear();return true;}
        if(command=="close_window"){auto e=resolve(a["target"]);if(!e.w)throw std::runtime_error("Window not found");e.w->window()->close();return true;}
        if(command=="screenshot"){
            auto e=a.contains("target")?resolve(a["target"]):a.contains("window")?resolve(a["window"]):Element{qApp->activeModalWidget()?qApp->activeModalWidget():qApp->activeWindow()};
            if(!e.w)throw std::runtime_error("No window to capture");QWidget*w=e.w->window();auto pix=w->screen()->grabWindow(w->winId());if(pix.isNull())pix=w->grab();
            auto scale=pix.devicePixelRatio();if(a.contains("target")&&yes(a,"crop",true)){auto r=e.rect();r.translate(-w->mapToGlobal({0,0}));int margin=num(a,"margin",num(a,"padding",80));r=r.adjusted(-margin,-margin,margin,margin).intersected(w->rect());pix=pix.copy(QRect(r.x()*scale,r.y()*scale,r.width()*scale,r.height()*scale));}
            int maxWidth=num(a,"max_width",1600);if(maxWidth>0&&pix.width()>maxWidth)pix=pix.scaledToWidth(maxWidth,Qt::SmoothTransformation);
            auto path=str(a,"path");if(path.isEmpty()||!pix.save(path,"PNG"))throw std::runtime_error("Screenshot save failed");return QJsonObject{{"path",path},{"width",pix.width()},{"height",pix.height()},{"device_pixel_ratio",scale}};
        }
        throw std::runtime_error(("Unknown Qt command: "+command).toStdString());
    }
};
Bridge*bridge=nullptr;
QByteArray dispatch(QByteArray request){try{auto doc=QJsonDocument::fromJson(request);if(!doc.isObject())throw std::runtime_error("Expected JSON object");if(!bridge)bridge=new Bridge;auto o=doc.object();auto result=validUnicode(bridge->call(str(o,"command"),o["args"].toObject()));return QJsonDocument(QJsonObject{{"ok",true},{"result",result}}).toJson(QJsonDocument::Compact);}catch(const std::exception&e){return QJsonDocument(QJsonObject{{"ok",false},{"error",e.what()}}).toJson(QJsonDocument::Compact);}}
}
extern "C" __declspec(dllexport) char* guide_qt_json(const char*json){
    QByteArray result;if(!qApp)return _strdup("{\"ok\":false,\"error\":\"Qt application is not running\"}");
    auto request=QByteArray(json);try{onGui([&]{result=dispatch(request);});}catch(const std::exception&e){result=QJsonDocument(QJsonObject{{"ok",false},{"error",e.what()}}).toJson(QJsonDocument::Compact);}
    return _strdup(result.constData());
}
extern "C" __declspec(dllexport) void guide_qt_free(void*p){free(p);}
extern "C" __declspec(dllexport) void guide_qt_invoke(void(__cdecl*callback)(void*),void*state){if(!qApp)return;try{onGui([=]{callback(state);});}catch(...){}}
