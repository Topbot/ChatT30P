angular.module('stats').controller('NavController', ["$scope", "$location", "$rootScope", function ($scope, $location, $rootScope) {
    $scope.isActive = function (viewLocation) {
        if (viewLocation == '/' && $location.path() == '/vkusers') return true;
        return viewLocation === $location.path() || $location.path().startsWith(viewLocation + "/");
    };
    $scope.UserVars = UserVars;
}]);

angular.module('stats').controller('SubNavController', ["$scope", "$location", "$rootScope", function ($scope, $location, $rootScope) {
    $scope.isActive = function (viewLocation) {
        return viewLocation === $location.path();
    };
    $scope.UserVars = UserVars;
}]);

if (typeof String.prototype.startsWith != 'function') {
    String.prototype.startsWith = function (str) {
        return this.slice(0, str.length) == str;
    };
}

function spinOn() {
    $("#spinner").removeClass("loaded");
    $("#spinner").addClass("loading");
}

function spinOff() {
    $("#spinner").removeClass("loading");
    $("#spinner").addClass("loaded");
}

function loading(id) {
    $("#" + id + "-spinner").removeClass("loaded");
    $("#" + id + "-spinner").addClass("loading");
}

function loaded(id) {
    $("#" + id + "-spinner").removeClass("loading");
    $("#" + id + "-spinner").addClass("loaded");
}

function rowSpinOff(items) {
    if (items.length > 0) {
        $('#tr-spinner').hide();
    }
    else {
        $('#div-spinner').html(BlogAdmin.i18n.empty);
    }
}

function selectedOption(arr, val) {
    for (var i = 0; i < arr.length; i++) {
        if (arr[i].OptionValue == val) return arr[i];
    }
}

function findInArray(arr, name, value) {
    for (var i = 0, len = arr.length; i < len; i++) {
        if (name in arr[i] && arr[i][name] == value) return arr[i];
    };
    return false;
}

function webRoot(url) {
    var result = "/";
    if (url.substring(0, 1) === "/") {
        return result + url.substring(1);
    }
    else {
        return result + url;
    }
}

function processChecked(url, action, scope, dataService) {
    spinOn();
    var i = scope.items.length;
    var checked = [];
    while (i--) {
        var item = scope.items[i];
        if (item.IsChecked === true) {
            checked.push(item);
        }
    }
    if (checked.length < 1) {
        return false;
    }
    dataService.processChecked(url + action, checked, function (data) {
        scope.load();
        toastr.success(BlogAdmin.i18n.completed);
        if ($('#chkAll')) {
            $('#chkAll').prop('checked', false);
        }
        spinOff();
    }, function (data) {
        toastr.error(BlogAdmin.i18n.failed);
        spinOff();
    });
}

function getFromQueryString(param) {
    var url = window.location.href.slice(window.location.href.indexOf('?') + 1).split('&');
    for (var i = 0; i < url.length; i++) {
        var urlparam = url[i].split('=');
        if (urlparam[0] == param) {
            return urlparam[1];
        }
    }
}

function arrayUnique(array) {
    var a = array.concat();
    for (var i = 0; i < a.length; ++i) {
        for (var j = i + 1; j < a.length; ++j) {
            if (a[i].Id === a[j].Id)
                a.splice(j--, 1);
        }
    }
    return a;
}

function beforeDataLoad() {
    var c = $('#counter');
    if(c.length > 0){
        var intervalId = setInterval(function(){
            // Time Out check
            var i = parseInt($('#counter').text());
            if (i <= 0) {
                clearInterval(intervalId);
            } else {
                //Otherwise
                c.text(i - 1);
            }
        }, 1000);
    }
}

function onDataLoad(scope, data) {
    scope.items = arrayUnique(scope.items.concat(data));
    if (scope.items.length > 0) {
        var maxvalue = scope.items[0].Members !== undefined ? scope.items[0].Members : (scope.items[0].M !== undefined ? scope.items[0].M : 100);
        if (scope.sliderConfig !== undefined) {
            scope.sliderConfig.max = maxvalue;
            if (scope.sliderConfig.disabled) { //первый раз
                if (angular.isFunction(scope.sliderConfig.userMax)) {
                    scope.sliderConfig.userMax(maxvalue);
                }
                //scope.sliderConfig.step = Math.floor(scope.items[0].Members / 10000) * 10;
                scope.sliderConfig.disabled = false;
            }
        }
    }
    //scope.requestFromServer = true;
    scope.finishedFromServer = true;
}